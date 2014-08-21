﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ProjectInitializer.cs" company="Orchestra development team">
//   Copyright (c) 2008 - 2014 Orchestra development team. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------


namespace Orc.ProjectManagement
{
    using Catel;
    using Services;

    public class FileProjectInitializer : IProjectInitializer
    {
        private readonly ICommandLineService _commandLineService;

        public FileProjectInitializer(ICommandLineService commandLineService)
        {
            Argument.IsNotNull(() => commandLineService);

            _commandLineService = commandLineService;
        }

        public virtual string GetInitialLocation()
        {
            string filePath = null;

            if (_commandLineService.Arguments.Length > 0)
            {
                filePath = _commandLineService.Arguments[0];
            }

            return filePath;
        }
    }
}