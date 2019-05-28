//------------------------------------------------------------------------------
// <copyright file="RunWithoutDebuggingPackage.cs" company="BhaaL">
//     Copyright (c) BhaaL.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using GitHub.BhaaLseN.VSIX.Commands;
using GitHub.BhaaLseN.VSIX.Converters;
using GitHub.BhaaLseN.VSIX.SourceControl;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using DTE = EnvDTE.DTE;
using MVSS = Microsoft.VisualStudio.Shell;

namespace GitHub.BhaaLseN.VSIX
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [MVSS.PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [MVSS.InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)] // Info on this package for Help/About
    [MVSS.ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, MVSS.PackageAutoLoadFlags.BackgroundLoad)]
    [MVSS.ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuidString)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    public sealed class VSXPackage : MVSS.AsyncPackage
    {
        // this DTE member is required to keep the com object reference alive; otherwise the events may not fire when the other side is collected.
        private DTE _dte;
        private SolutionEventListener _solutionEventListener;
        private string _originalWindowTitle;
        private BindingExpression _titleBindingExpression;
        private SourceControlWatcher _sourceControlWatcher;

        private SourceControlWatcher SourceControlWatcher
        {
            get { return _sourceControlWatcher; }
            set
            {
                if (_sourceControlWatcher != null)
                {
                    _sourceControlWatcher.BranchNameChanged -= OnBranchNameChanged;
                    _sourceControlWatcher.Dispose();
                }

                _sourceControlWatcher = value;

                if (_sourceControlWatcher != null)
                    _sourceControlWatcher.BranchNameChanged += OnBranchNameChanged;
            }
        }

        /// <summary>RunWithoutDebuggingPackage GUID string.</summary>
        public const string PackageGuidString = "460ec7d5-539f-4e0a-bd83-4032a409c081";

        /// <summary>Initializes a new instance of the <see cref="VSXPackage"/> class.</summary>
        public VSXPackage()
        {
        }

        private void SolutionOpened()
        {
            PrepareSourceControlWatcher();
        }

        private void PrepareSourceControlWatcher()
        {
            if (_dte.Solution.IsOpen)
            {
                string solutionFilePath = _dte.Solution.FullName;
                // initialize SCW if we don't have one yet
                if (SourceControlWatcher == null)
                    SourceControlWatcher = SourceControlWatcher.Create(solutionFilePath);
                // update SCW if it is a different solution
                else if (!string.Equals(SourceControlWatcher.SolutionDirectory, Path.GetFullPath(Path.GetDirectoryName(solutionFilePath)), StringComparison.InvariantCultureIgnoreCase))
                    SourceControlWatcher = SourceControlWatcher.Create(solutionFilePath);
            }
            else
            {
                // no solution, no source control.
                SourceControlWatcher = null;
                BranchName = null;
                UpdateMainWindowTitle();
            }

            // still no SCW? most likely means we're not under source control; or the SCM is not supported at this point.
            if (SourceControlWatcher == null)
                return;

            // remember the original window title, we might need it later
            _originalWindowTitle = Application.Current.MainWindow.Title;
            BranchName = SourceControlWatcher.BranchName;
            UpdateMainWindowTitle();
        }

        private bool _thatsMeChangingTheTitle;
        private void OnMainWindowTitleChanged(object sender, EventArgs e)
        {
            if (_thatsMeChangingTheTitle)
                return;

            PrepareSourceControlWatcher();
        }

        private void OnBranchNameChanged(object sender, EventArgs e)
        {
            BranchName = SourceControlWatcher.BranchName;
            UpdateMainWindowTitle();
        }

        internal string BranchName { get; private set; }
        private void UpdateMainWindowTitle()
        {
            if (_titleBindingExpression != null)
            {
                // force converter update
                Application.Current.Dispatcher.InvokeAsync(_titleBindingExpression.UpdateTarget, DispatcherPriority.DataBind);
            }
            else
            {
                // no binding? just set the window title by hand.
                if (!string.IsNullOrEmpty(BranchName))
                    SetWindowTitle(string.Format("{0} - {1}", BranchName, _originalWindowTitle));
            }
        }

        private void SetWindowTitle(string newTitle)
        {
            try
            {
                _thatsMeChangingTheTitle = true;
                Application.Current.MainWindow.Title = newTitle;
            }
            finally
            {
                _thatsMeChangingTheTitle = false;
            }
        }

        private async Task<bool> IsSolutionLoadedAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var solService = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;

            ErrorHandler.ThrowOnFailure(solService.GetProperty((int)__VSPROPID.VSPROPID_IsSolutionOpen, out object value));

            return value is bool isSolOpen && isSolOpen;
        }

        private async Task InitializeSolutionTracking(CancellationToken cancellationToken)
        {
            _dte = (DTE)await GetServiceAsync(typeof(DTE));
            _dte.Events.SolutionEvents.Opened += SolutionOpened;
            _solutionEventListener = new SolutionEventListener((IVsSolution)await GetServiceAsync(typeof(SVsSolution)));
            _solutionEventListener.AfterSolutionLoaded += SolutionOpened;

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            // grab the current main window binding for Title. it shouldn't be null, since VS uses WPF Bindings.
            // if it happens to be unbound, we just revert to setting the main window title directly
            var titleBindingExpression = Application.Current.MainWindow.GetBindingExpression(Window.TitleProperty);
            if (titleBindingExpression != null && titleBindingExpression.ParentBinding != null)
            {
                var titleBinding = titleBindingExpression.ParentBinding;
                // duplicate the binding and insert our own converter to prepend the branch name
                var newTitleBinding = new Binding
                {
                    Converter = new SourceControlWindowTitleConverter(this, titleBinding.Converter),
                    Path = titleBinding.Path,
                };

                Application.Current.MainWindow.SetBinding(Window.TitleProperty, newTitleBinding);
                // remember the binding expression so we can force an update when the branch name changes
                _titleBindingExpression = Application.Current.MainWindow.GetBindingExpression(Window.TitleProperty);
            }

            // grab the title property descriptor so we can attach a value changed handler
            var titlePropertyDescriptor = DependencyPropertyDescriptor.FromProperty(Window.TitleProperty, typeof(Window));
            titlePropertyDescriptor.AddValueChanged(Application.Current.MainWindow, OnMainWindowTitleChanged);

            // Since this package might not be initialized until after a solution has finished loading,
            // we need to check if a solution has already been loaded and then handle it.
            bool isSolutionLoaded = await IsSolutionLoadedAsync();
            if (isSolutionLoaded)
                SolutionOpened();
        }

        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<MVSS.ServiceProgressData> progress)
        {
            await RunWithoutDebugging.InitializeAsync(this);
            await InitializeSolutionTracking(cancellationToken);
            await base.InitializeAsync(cancellationToken, progress);
        }
        #endregion
    }
}
