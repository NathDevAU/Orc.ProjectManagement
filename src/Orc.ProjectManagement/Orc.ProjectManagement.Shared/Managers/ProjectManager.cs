﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ProjectManager.cs" company="WildGums">
//   Copyright (c) 2008 - 2015 WildGums. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------


namespace Orc.ProjectManagement
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Catel;
    using Catel.Collections;
    using Catel.Data;
    using Catel.IoC;
    using Catel.Logging;
    using Catel.Reflection;
    using Catel.Threading;

    public class ProjectManager : IProjectManager, INeedCustomInitialization
    {
        #region Fields
        private static readonly ILog Log = LogManager.GetCurrentClassLogger();
        private readonly IProjectInitializer _projectInitializer;
        private readonly IProjectManagementInitializationService _projectManagementInitializationService;
        private readonly IProjectStateSetter _projectStateSetter;
        private readonly IDictionary<string, IProjectRefresher> _projectRefreshers;
        private readonly IProjectRefresherSelector _projectRefresherSelector;
        private readonly ListDictionary<string, IProject> _projects;
        private readonly IProjectSerializerSelector _projectSerializerSelector;
        private readonly IProjectValidator _projectValidator;
        private readonly IProjectUpgrader _projectUpgrader;

        private readonly Dictionary<string, AsyncLock> _projectOperationLockers = new Dictionary<string, AsyncLock>();
        private readonly Dictionary<string, int> _projectOperationRefCounts = new Dictionary<string, int>();
        private readonly AsyncLock _commonAsyncLock = new AsyncLock();

        private readonly HashSet<string> _loadingProjects = new HashSet<string>();
        private readonly HashSet<string> _savingProjects = new HashSet<string>();
        #endregion

        #region Constructors
        public ProjectManager(IProjectValidator projectValidator, IProjectUpgrader projectUpgrader, IProjectRefresherSelector projectRefresherSelector,
            IProjectSerializerSelector projectSerializerSelector, IProjectInitializer projectInitializer, IProjectManagementConfigurationService projectManagementConfigurationService,
            IProjectManagementInitializationService projectManagementInitializationService, IProjectStateService projectStateService)
        {
            Argument.IsNotNull(() => projectValidator);
            Argument.IsNotNull(() => projectUpgrader);
            Argument.IsNotNull(() => projectRefresherSelector);
            Argument.IsNotNull(() => projectSerializerSelector);
            Argument.IsNotNull(() => projectInitializer);
            Argument.IsNotNull(() => projectManagementConfigurationService);
            Argument.IsNotNull(() => projectManagementInitializationService);
            Argument.IsNotNull(() => projectStateService);

            _projectValidator = projectValidator;
            _projectUpgrader = projectUpgrader;
            _projectRefresherSelector = projectRefresherSelector;
            _projectSerializerSelector = projectSerializerSelector;
            _projectInitializer = projectInitializer;
            _projectManagementInitializationService = projectManagementInitializationService;
            _projectStateSetter = (IProjectStateSetter)projectStateService;

            _projects = new ListDictionary<string, IProject>();
            _projectRefreshers = new ConcurrentDictionary<string, IProjectRefresher>();

            ProjectManagementType = projectManagementConfigurationService.GetProjectManagementType();
        }
        #endregion

        #region Properties
        public ProjectManagementType ProjectManagementType { get; private set; }

        public virtual IEnumerable<IProject> Projects
        {
            get { return _projects.Select(x => x.Value); }
        }

        public virtual IProject ActiveProject { get; set; }

        public bool IsLoading => _loadingProjects.Any();
        #endregion

        #region Methods
        void INeedCustomInitialization.Initialize()
        {
            _projectManagementInitializationService.Initialize(this);
        }
        #endregion

        #region Events
        public event AsyncEventHandler<ProjectCancelEventArgs> ProjectLoadingAsync;
        public event AsyncEventHandler<ProjectErrorEventArgs> ProjectLoadingFailedAsync;
        public event AsyncEventHandler<ProjectEventArgs> ProjectLoadingCanceledAsync;
        public event AsyncEventHandler<ProjectEventArgs> ProjectLoadedAsync;

        public event AsyncEventHandler<ProjectCancelEventArgs> ProjectSavingAsync;
        public event AsyncEventHandler<ProjectErrorEventArgs> ProjectSavingFailedAsync;
        public event AsyncEventHandler<ProjectEventArgs> ProjectSavingCanceledAsync;
        public event AsyncEventHandler<ProjectEventArgs> ProjectSavedAsync;

        public event AsyncEventHandler<ProjectCancelEventArgs> ProjectRefreshingAsync;
        public event AsyncEventHandler<ProjectEventArgs> ProjectRefreshedAsync;
        public event AsyncEventHandler<ProjectEventArgs> ProjectRefreshingCanceledAsync;
        public event AsyncEventHandler<ProjectErrorEventArgs> ProjectRefreshingFailedAsync;

        public event AsyncEventHandler<ProjectCancelEventArgs> ProjectClosingAsync;
        public event AsyncEventHandler<ProjectEventArgs> ProjectClosingCanceledAsync;
        public event AsyncEventHandler<ProjectEventArgs> ProjectClosedAsync;

        public event AsyncEventHandler<ProjectUpdatingCancelEventArgs> ProjectActivationAsync;
        public event AsyncEventHandler<ProjectUpdatedEventArgs> ProjectActivatedAsync;
        public event AsyncEventHandler<ProjectEventArgs> ProjectActivationCanceledAsync;
        public event AsyncEventHandler<ProjectErrorEventArgs> ProjectActivationFailedAsync;
        #endregion

        #region IProjectManager Members
        public async Task InitializeAsync()
        {
            var locations = _projectInitializer.GetInitialLocations().Where(x => !string.IsNullOrWhiteSpace(x));

            foreach (var location in locations)
            {
                Log.Debug("Loading initial project from location '{0}'", location);
                await LoadAsync(location).ConfigureAwait(false);
            }
        }

        public Task<bool> RefreshAsync()
        {
            var project = ActiveProject;

            return project == null
                ? TaskHelper<bool>.FromResult(false)
                : RefreshAsync(project);
        }

        public Task<bool> RefreshAsync(IProject project)
        {
            Argument.IsNotNull(() => project);

            return SynchroniseProjectOperationAsync(project.Location, () => SyncedRefreshAsync(project));
        }

        public Task<bool> LoadAsync(string location)
        {
            Argument.IsNotNullOrWhitespace("location", location);

            return SynchroniseProjectOperationAsync(location, async () =>
            {
                var project = await SyncedLoadProjectAsync(location);

                if (project != null)
                {
                    await SetActiveProjectAsync(project);
                }

                return project != null;
            });
        }

        public Task<bool> LoadInactiveAsync(string location)
        {
            Argument.IsNotNullOrWhitespace("location", location);

            return SynchroniseProjectOperationAsync(location, async () =>
            {
                var project = await SyncedLoadProjectAsync(location);

                return project != null;
            });
        }

        public Task<bool> SaveAsync(string location = null)
        {
            var project = ActiveProject;
            if (project == null)
            {
                Log.Error("Cannot save empty project");
                return TaskHelper<bool>.FromResult(false);
            }

            return SaveAsync(project, location);
        }

        public Task<bool> SaveAsync(IProject project, string location = null)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                location = project.Location;
            }

            return SynchroniseProjectOperationAsync(location, () => SyncedSaveAsync(project, location));
        }

        public Task<bool> CloseAsync()
        {
            var project = ActiveProject;

            return project == null
                ? TaskHelper<bool>.FromResult(false)
                : CloseAsync(project);
        }

        public Task<bool> CloseAsync(IProject project)
        {
            Argument.IsNotNull(() => project);

            var location = project.Location;

            return SynchroniseProjectOperationAsync(location, () => SyncedCloseAsync(project));
        }

        public async Task<bool> SetActiveProjectAsync(IProject project)
        {
            using (await _commonAsyncLock.LockAsync())
            {
                var activeProject = ActiveProject;

                if (project != null && !Projects.Contains(project))
                {
                    return false;
                }

                var activeProjectLocation = activeProject?.Location;
                var newProjectLocation = project?.Location;

                if (string.Equals(activeProjectLocation, newProjectLocation))
                {
                    return false;
                }

                Log.Info(project != null
                    ? $"Activating project '{project.Location}'"
                    : "Deactivating currently active project");

                var eventArgs = new ProjectUpdatingCancelEventArgs(activeProject, project);

                _projectStateSetter.SetProjectDeactivating(activeProject?.Location, true);
                _projectStateSetter.SetProjectActivating(project?.Location, true);

                await ProjectActivationAsync.SafeInvokeAsync(this, eventArgs, false).ConfigureAwait(false);

                if (eventArgs.Cancel)
                {
                    Log.Info(project != null
                        ? $"Activating project '{project.Location}' was canceled"
                        : "Deactivating currently active project");

                    _projectStateSetter.SetProjectActivating(project?.Location, false);

                    await ProjectActivationCanceledAsync.SafeInvokeAsync(this, new ProjectEventArgs(project)).ConfigureAwait(false);
                    return false;
                }

                Exception exception = null;

                try
                {
                    ActiveProject = project;
                }
                catch (Exception ex)
                {
                    exception = ex;
                }

                if (exception != null)
                {
                    Log.Error(exception, project != null
                        ? $"Failed to activate project '{project.Location}'"
                        : "Failed to deactivate currently active project");

                    _projectStateSetter.SetProjectActivating(project?.Location ?? string.Empty, false);
                    await ProjectActivationFailedAsync.SafeInvokeAsync(this, new ProjectErrorEventArgs(project, exception)).ConfigureAwait(false);
                    return false;
                }

                _projectStateSetter.SetProjectDeactivating(activeProject?.Location, false);
                _projectStateSetter.SetProjectActivating(project?.Location, false);

                await ProjectActivatedAsync.SafeInvokeAsync(this, new ProjectUpdatedEventArgs(activeProject, project)).ConfigureAwait(false);

                Log.Debug(project != null
                    ? $"Activating project '{project.Location}' was canceled"
                    : "Deactivating currently active project");

                return true;
            }
        }

        protected virtual async Task<IProject> ReadProjectAsync(string location)
        {
            var projectReader = _projectSerializerSelector.GetReader(location);
            if (projectReader == null)
            {
                throw Log.ErrorAndCreateException<InvalidOperationException>($"No project reader is found for location '{location}'");
            }

            Log.Debug("Using project reader '{0}'", projectReader.GetType().Name);

            var project = await projectReader.ReadAsync(location).ConfigureAwait(false);

            return project;
        }

        protected virtual Task<bool> WriteProjectAsync(IProject project, string location)
        {
            var projectWriter = _projectSerializerSelector.GetWriter(location);
            if (projectWriter == null)
            {
                throw new NotSupportedException($"No project writer is found for location '{location}'");
            }

            Log.Debug("Using project writer '{0}'", projectWriter.GetType().Name);

            return projectWriter.WriteAsync(project, location);
        }

        private async Task<T> SynchroniseProjectOperationAsync<T>(string projectLocation, Func<Task<T>> operation)
        {
            Argument.IsNotNullOrEmpty(() => projectLocation);

            AsyncLock asyncLock;
            lock (_projectOperationLockers)
            {
                if (!_projectOperationLockers.TryGetValue(projectLocation, out asyncLock))
                {
                    asyncLock = new AsyncLock();
                    _projectOperationLockers.Add(projectLocation, asyncLock);
                }

                if (!_projectOperationRefCounts.TryGetValue(projectLocation, out int refCount))
                {
                    refCount = 0;
                }

                refCount++;

                _projectOperationRefCounts[projectLocation] = refCount;
            }

            try
            {
                using (await asyncLock.LockAsync())
                {
                    return await operation();
                }
            }
            finally
            {
                lock (_projectOperationLockers)
                {
                    if (!_projectOperationRefCounts.TryGetValue(projectLocation, out int refCount))
                    {
                        refCount = 0;
                    }

                    refCount--;

                    if (refCount >= 1)
                    {
                        _projectOperationRefCounts[projectLocation] = refCount;
                    }
                    else
                    {
                        _projectOperationRefCounts.Remove(projectLocation);
                        _projectOperationLockers.Remove(projectLocation);
                    }
                }
            }
        }

        private async Task<bool> SyncedRefreshAsync(IProject project)
        {
            var projectLocation = project.Location;

            var activeProjectLocation = this.GetActiveProjectLocation();

            Log.Debug("Refreshing project from '{0}'", projectLocation);

            var isRefreshingActiveProject = activeProjectLocation.EndsWithIgnoreCase(projectLocation);

            _projectStateSetter.SetProjectRefreshing(projectLocation, true, isRefreshingActiveProject);

            var cancelEventArgs = new ProjectCancelEventArgs(project);
            await ProjectRefreshingAsync.SafeInvokeAsync(this, cancelEventArgs, false).ConfigureAwait(false);

            Exception error = null;
            IValidationContext validationContext = null;

            try
            {
                if (cancelEventArgs.Cancel)
                {
                    _projectStateSetter.SetProjectRefreshing(projectLocation, false, true);

                    await ProjectRefreshingCanceledAsync.SafeInvokeAsync(this, new ProjectErrorEventArgs(project)).ConfigureAwait(false);
                    return false;
                }

                UnregisterProject(project);

                if (isRefreshingActiveProject)
                {
                    await SetActiveProjectAsync(null).ConfigureAwait(false);
                }

                validationContext = await _projectValidator.ValidateProjectBeforeLoadingAsync(projectLocation);
                if (validationContext.HasErrors)
                {
                    throw Log.ErrorAndCreateException<InvalidOperationException>($"Project could not be loaded from '{projectLocation}', the validator returned errors");
                }

                var loadedProject = await QuietlyLoadProjectAsync(projectLocation, false).ConfigureAwait(false);

                validationContext = await _projectValidator.ValidateProjectAsync(loadedProject);
                if (validationContext.HasErrors)
                {
                    throw Log.ErrorAndCreateException<InvalidOperationException>($"Project data was loaded from '{projectLocation}', but the validator returned errors");
                }

                RegisterProject(loadedProject);

                // Note: we disable IsRefreshingActiveProject at Activated event, that is why isActiveProject == false
                _projectStateSetter.SetProjectRefreshing(projectLocation, true, false);

                await ProjectRefreshedAsync.SafeInvokeAsync(this, new ProjectEventArgs(loadedProject)).ConfigureAwait(false);

                if (isRefreshingActiveProject)
                {
                    await SetActiveProjectAsync(loadedProject).ConfigureAwait(false);
                }

                Log.Info("Refreshed project from '{0}'", projectLocation);
            }
            catch (Exception exception)
            {
                error = exception;
            }

            if (error == null)
            {
                return true;
            }

            _projectStateSetter.SetProjectRefreshing(projectLocation, false, true);

            var eventArgs = new ProjectErrorEventArgs(project,
                new ProjectException(project, $"Failed to load project from location '{projectLocation}' while refreshing.", error),
                validationContext);

            await ProjectRefreshingFailedAsync.SafeInvokeAsync(this, eventArgs).ConfigureAwait(false);

            return false;
        }

        private async Task<IProject> SyncedLoadProjectAsync(string location)
        {
            Argument.IsNotNullOrWhitespace("location", location);

            var project = Projects.FirstOrDefault(x => location.EqualsIgnoreCase(x.Location));
            if (project != null)
            {
                return project;
            }

            var projectLocation = location;
            using (new DisposableToken(null, token => _loadingProjects.Add(projectLocation), token => _loadingProjects.Remove(projectLocation)))
            {
                Log.Debug($"Going to load project from '{location}', checking if an upgrade is required");

                if (await _projectUpgrader.RequiresUpgradeAsync(location))
                {
                    Log.Debug($"Upgrade is required for '{location}', upgrading...");

                    location = await _projectUpgrader.UpgradeAsync(location);

                    Log.Debug($"Upgraded project, final location is '{location}'");
                }

                Log.Debug($"Loading project from '{location}'");

                _projectStateSetter.SetProjectLoading(location, true);

                var cancelEventArgs = new ProjectCancelEventArgs(location);

                await ProjectLoadingAsync.SafeInvokeAsync(this, cancelEventArgs, false).ConfigureAwait(false);

                if (cancelEventArgs.Cancel)
                {
                    Log.Debug("Canceled loading of project from '{0}'", location);

                    _projectStateSetter.SetProjectLoading(location, false);

                    await ProjectLoadingCanceledAsync.SafeInvokeAsync(this, new ProjectEventArgs(location)).ConfigureAwait(false);

                    return null;
                }

                Exception error = null;

                IValidationContext validationContext = null;

                try
                {
                    if (_projects.Count > 0 && ProjectManagementType == ProjectManagementType.SingleDocument)
                    {
                        throw Log.ErrorAndCreateException<SdiProjectManagementException>("Cannot load project '{0}', currently in SDI mode", location);
                    }

                    if (!await _projectValidator.CanStartLoadingProjectAsync(location))
                    {
                        validationContext = new ValidationContext();
                        validationContext.Add(BusinessRuleValidationResult.CreateError("Project validator informed that project could not be loaded"));

                        throw new ProjectException(location, $"Cannot load project from '{location}'");
                    }

                    validationContext = await _projectValidator.ValidateProjectBeforeLoadingAsync(location);
                    if (validationContext.HasErrors)
                    {
                        throw Log.ErrorAndCreateException<InvalidOperationException>($"Project could not be loaded from '{location}', validator returned errors");
                    }

                    project = await QuietlyLoadProjectAsync(location, true).ConfigureAwait(false);

                    validationContext = await _projectValidator.ValidateProjectAsync(project);
                    if (validationContext.HasErrors)
                    {
                        throw Log.ErrorAndCreateException<InvalidOperationException>($"Project data was loaded from '{location}', but the validator returned errors");
                    }

                    RegisterProject(project);
                }
                catch (Exception ex)
                {
                    error = ex;
                    Log.Error(ex, "Failed to load project from '{0}'", location);
                }

                if (error != null)
                {
                    _projectStateSetter.SetProjectLoading(location, false);

                    await ProjectLoadingFailedAsync.SafeInvokeAsync(this, new ProjectErrorEventArgs(location, error, validationContext)).ConfigureAwait(false);

                    return null;
                }

                _projectStateSetter.SetProjectLoading(project?.Location, false);

                await ProjectLoadedAsync.SafeInvokeAsync(this, new ProjectEventArgs(project)).ConfigureAwait(false);

                Log.Info("Loaded project from '{0}'", location);
            }

            return project;
        }

        private async Task<bool> SyncedSaveAsync(IProject project, string location)
        {
            Argument.IsNotNull(() => project);

            if (string.IsNullOrWhiteSpace(location))
            {
                location = project.Location;
            }

            using (new DisposableToken(null, token => _savingProjects.Add(location), token => _savingProjects.Remove(location)))
            {
                Log.Debug("Saving project '{0}' to '{1}'", project, location);

                // TODO: Support SaveAs where we store the new location, but we need to make sure that we also remove 
                // the old one (and revert on failure & cancel). For now this is sufficient (we will just get a new instance)
                _projectStateSetter.SetProjectSaving(location, true);

                var cancelEventArgs = new ProjectCancelEventArgs(project);
                await ProjectSavingAsync.SafeInvokeAsync(this, cancelEventArgs, false).ConfigureAwait(false);

                if (cancelEventArgs.Cancel)
                {
                    _projectStateSetter.SetProjectSaving(location, false);

                    Log.Debug("Canceled saving of project to '{0}'", location);
                    await ProjectSavingCanceledAsync.SafeInvokeAsync(this, new ProjectEventArgs(project)).ConfigureAwait(false);
                    return false;
                }

                Exception error = null;
                var success = true;
                try
                {
                    success = await WriteProjectAsync(project, location);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                if (error != null)
                {
                    _projectStateSetter.SetProjectSaving(location, false);

                    Log.Error(error, "Failed to save project '{0}' to '{1}'", project, location);
                    await ProjectSavingFailedAsync.SafeInvokeAsync(this, new ProjectErrorEventArgs(project, error)).ConfigureAwait(false);

                    return false;
                }

                if (!success)
                {
                    Log.Error("Not saved project '{0}' to '{1}'", project, location);
                    return false;
                }

                _projectStateSetter.SetProjectSaving(location, false);

                await ProjectSavedAsync.SafeInvokeAsync(this, new ProjectEventArgs(project)).ConfigureAwait(false);

                var projectString = project.ToString();
                Log.Info("Saved project '{0}' to '{1}'", projectString, location);
            }

            return true;
        }

        private async Task<bool> SyncedCloseAsync(IProject project)
        {
            Argument.IsNotNull(() => project);

            Log.Debug("Closing project '{0}'", project);

            _projectStateSetter.SetProjectClosing(project.Location, true);

            var cancelEventArgs = new ProjectCancelEventArgs(project);
            await ProjectClosingAsync.SafeInvokeAsync(this, cancelEventArgs, false).ConfigureAwait(false);

            if (cancelEventArgs.Cancel)
            {
                _projectStateSetter.SetProjectClosing(project.Location, false);

                Log.Debug("Canceled closing project '{0}'", project);
                await ProjectClosingCanceledAsync.SafeInvokeAsync(this, new ProjectEventArgs(project)).ConfigureAwait(false);
                return false;
            }

            if (Equals(ActiveProject, project))
            {
                await SetActiveProjectAsync(null).ConfigureAwait(false);
            }

            UnregisterProject(project);

            _projectStateSetter.SetProjectClosing(project.Location, false);
            await ProjectClosedAsync.SafeInvokeAsync(this, new ProjectEventArgs(project)).ConfigureAwait(false);

            Log.Info("Closed project '{0}'", project);

            return true;
        }

        private void RegisterProject(IProject project)
        {
            var projectLocation = project.Location;
            _projects[projectLocation] = project;

            InitializeProjectRefresher(projectLocation);
        }

        private async Task<IProject> QuietlyLoadProjectAsync(string location, bool skipCanLoadValidation)
        {
            if (skipCanLoadValidation)
            {
                Log.Debug("Validating to see if we can load the project from '{0}'", location);

                if (!await _projectValidator.CanStartLoadingProjectAsync(location))
                {
                    throw new ProjectException(location, $"Cannot load project from '{location}'");
                }
            }

            var project = await ReadProjectAsync(location);

            if (project == null)
            {
                throw Log.ErrorAndCreateException<InvalidOperationException>($"Project could not be loaded from '{location}'");
            }

            return project;
        }

        private void UnregisterProject(IProject project)
        {
            var location = project.Location;
            if (_projects.ContainsKey(location))
            {
                _projects.Remove(location);
            }

            ReleaseProjectRefresher(project);
        }

        private void InitializeProjectRefresher(string projectLocation)
        {
            IProjectRefresher projectRefresher;

            if (!_projectRefreshers.TryGetValue(projectLocation, out projectRefresher) || projectRefresher == null)
            {
                try
                {
                    projectRefresher = _projectRefresherSelector.GetProjectRefresher(projectLocation);

                    if (projectRefresher != null)
                    {
                        Log.Debug("Subscribing to project refresher '{0}'", projectRefresher.GetType().GetSafeFullName(false));

                        projectRefresher.Updated += OnProjectRefresherUpdated;
                        projectRefresher.Subscribe();

                        _projectRefreshers[projectLocation] = projectRefresher;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to subscribe to project refresher");
                    throw;
                }
            }
        }

        private void ReleaseProjectRefresher(IProject project)
        {
            IProjectRefresher projectRefresher;

            var location = project.Location;

            if (_projectRefreshers.TryGetValue(location, out projectRefresher) && projectRefresher != null)
            {
                try
                {
                    Log.Debug("Unsubscribing from project refresher '{0}'", projectRefresher.GetType().GetSafeFullName(false));

                    projectRefresher.Unsubscribe();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to unsubscribe from project refresher");
                }

                projectRefresher.Updated -= OnProjectRefresherUpdated;

                _projectRefreshers.Remove(location);
            }
        }

        private void OnProjectRefresherUpdated(object sender, ProjectEventArgs e)
        {
            var projectLocation = e.Location;
            if (_loadingProjects.Contains(projectLocation) || _savingProjects.Contains(projectLocation))
            {
                return;
            }

            // Note: not sure why we still need this
        }
        #endregion
    }
}