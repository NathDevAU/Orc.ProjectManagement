﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="DirectoryExistsProjectValidator.cs" company="Orchestra development team">
//   Copyright (c) 2008 - 2014 Orchestra development team. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------


namespace Orc.ProjectManagement
{
    using System.IO;
    using System.Threading.Tasks;

    public class DirectoryExistsProjectValidator : ProjectValidatorBase
    {
        #region IProjectValidator Members
        public override async Task<bool> CanStartLoadingProject(string location)
        {
            return Directory.Exists(location);
        }
        #endregion
    }
}