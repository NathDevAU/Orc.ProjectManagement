﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ProjectUpdatedEventArgsFacts.cs" company="Orchestra development team">
//   Copyright (c) 2008 - 2014 Orchestra development team. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------


namespace Orc.ProjectManagement.Test.EventArgs
{
    using Mocks;
    using NUnit.Framework;

    [TestFixture]
    public class ProjectUpdatedEventArgsFacts
    {
        [TestCase(null, null, false)]
        [TestCase(null, "B", false)]
        [TestCase("A", null, false)]
        [TestCase("A", "B", false)]
        [TestCase("A", "A", true)]
        public void TheIsRefreshProperty(string projectLocation1, string projectLocation2, bool expectedResult)
        {
            var project1 = !string.IsNullOrEmpty(projectLocation1) ? new Project(projectLocation1) : null;
            var project2 = !string.IsNullOrEmpty(projectLocation2) ? new Project(projectLocation2) : null;

            var projectUpdatedEventArgs = new ProjectUpdatedEventArgs(project1, project2);

            Assert.AreEqual(expectedResult, projectUpdatedEventArgs.IsRefresh);
        }
    }
}