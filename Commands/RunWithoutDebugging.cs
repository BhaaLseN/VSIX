//------------------------------------------------------------------------------
// <copyright file="RunWithoutDebugging.cs" company="BhaaL">
//     Copyright (c) BhaaL.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.Shell.Interop;
using MVSS = Microsoft.VisualStudio.Shell;
using Process = System.Diagnostics.Process;

namespace GitHub.BhaaLseN.VSIX.Commands
{
    /// <summary>Command handler for "Run without debugging"</summary>
    internal sealed class RunWithoutDebugging
    {
        /// <summary>Command ID.</summary>
        public const int CommandId = 0x0100;

        /// <summary>Command menu group (command set GUID).</summary>
        public static readonly Guid CommandSet = new Guid("2117f9a0-9a36-47a8-8c58-b3d6994dab1f");

        /// <summary>VS Package that provides this command, not null.</summary>
        private readonly MVSS.AsyncPackage _package;

        /// <summary>
        /// Initializes a new instance of the <see cref="RunWithoutDebugging"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        private RunWithoutDebugging(MVSS.AsyncPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));

        }

        /// <summary>Gets the instance of the command.</summary>
        public static RunWithoutDebugging Instance { get; private set; }

        /// <summary>Initializes the singleton instance of the command.</summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(MVSS.AsyncPackage package)
        {
            Instance = new RunWithoutDebugging(package);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as MVSS.OleMenuCommandService;
            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(Instance.MenuItemCallback, menuCommandID);
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private async void MenuItemCallback(object sender, EventArgs e)
        {
            var (selectedProject, executableFilePath, arguments, workingDirectory) = await GetSelectedProjectData();
            if (selectedProject == null)
            {
                MVSS.VsShellUtilities.ShowMessageBox(
                    _package,
                    "You did not select a project, or something else failed. Sorry about that.",
                    "Start without debugging",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

                return;
            }

            // trigger a build unless we're already debugging
            if (selectedProject.DTE.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
            {
                var solutionBuild = selectedProject.DTE.Solution.SolutionBuild;
                solutionBuild.BuildProject(solutionBuild.ActiveConfiguration.Name, selectedProject.UniqueName, true);
            }

            if (File.Exists(executableFilePath))
            {
                Process.Start(new ProcessStartInfo(executableFilePath, arguments)
                {
                    WorkingDirectory = workingDirectory,
                });
            }
            else
            {
                MVSS.VsShellUtilities.ShowMessageBox(
                    _package,
                    "Could not run this project, most likely there were build errors.",
                    "Start without debugging",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        private async Task<(Project SelectedProject, string ExecutableFilePath, string Arguments, string WorkingDirectory)> GetSelectedProjectData()
        {
            var vsMonitorSelection = (IVsMonitorSelection)await _package.GetServiceAsync(typeof(IVsMonitorSelection));
            if (ErrorHandler.Failed(vsMonitorSelection.GetCurrentSelection(out var hierarchyPtr, out _, out _, out var selectionContainerPtr))
                || hierarchyPtr == IntPtr.Zero)
            {
                return default;
            }

            var iVsProject = (IVsProject)Marshal.GetTypedObjectForIUnknown(hierarchyPtr, typeof(IVsProject));
            var iVsHierarchy = (IVsHierarchy)Marshal.GetTypedObjectForIUnknown(hierarchyPtr, typeof(IVsHierarchy));
            Marshal.Release(hierarchyPtr);
            Marshal.Release(selectionContainerPtr);

            if (ErrorHandler.Failed(iVsHierarchy.GetProperty((uint)VSConstants.VSITEMID.Root, (int)__VSHPROPID.VSHPROPID_ExtObject, out object projectObj)))
                return default;
            if (!(projectObj is Project project))
                return default;

            var browseContext = iVsProject as IVsBrowseObjectContext ?? project.Object as IVsBrowseObjectContext;
            if (browseContext == null)
            {
                // likely an old-style project, such as .NET Framework
                var projectProperties = project.Properties;
                var activeConfigurationProperties = project.ConfigurationManager.ActiveConfiguration.Properties;

                // assume an external program is started. most commonly used with class libraries
                string executableFilePath = activeConfigurationProperties.GetPropertyValue<string>("StartProgram");
                if (string.IsNullOrWhiteSpace(executableFilePath))
                {
                    // in case no external program is used, take the current executable path for the currently active configuration instead
                    string fullPath = projectProperties.GetPropertyValue<string>("FullPath");
                    string outputFileName = projectProperties.GetPropertyValue<string>("OutputFileName");
                    string activeConfigurationOutputPath = activeConfigurationProperties.GetPropertyValue<string>("OutputPath");
                    executableFilePath = Path.Combine(fullPath, activeConfigurationOutputPath, outputFileName);
                }

                // grab the configured working directory, or just use the application directory as fallback
                string workingDirectory = activeConfigurationProperties.GetPropertyValue<string>("StartWorkingDirectory");
                if (string.IsNullOrWhiteSpace(workingDirectory))
                    workingDirectory = Path.GetDirectoryName(executableFilePath);

                string arguments = activeConfigurationProperties.GetPropertyValue<string>("StartArguments");

                return (project, executableFilePath, arguments, workingDirectory);
            }

            // still here? this is a project supported by the Common Project System (CPS; such as .NET Core/.NET 5+)
            var unconfiguredProject = browseContext.UnconfiguredProject;
            var configuredProject = await unconfiguredProject.GetSuggestedConfiguredProjectAsync();
            if (configuredProject == null)
                return default;

            string runCommand;
            string runWorkingDirectory;
            string runArguments;
            var projectLockService = unconfiguredProject.ProjectService.Services.ProjectLockService;
            using (var access = await projectLockService.ReadLockAsync())
            {
                var msBuildProject = await access.GetProjectAsync(configuredProject);
                runCommand = msBuildProject.GetPropertyValue("RunCommand");
                runWorkingDirectory = msBuildProject.GetPropertyValue("RunWorkingDirectory");
                runArguments = msBuildProject.GetPropertyValue("RunArguments");
            }

            return (project, runCommand, runArguments, runWorkingDirectory);
        }
    }
}
