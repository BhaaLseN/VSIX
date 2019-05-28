//------------------------------------------------------------------------------
// <copyright file="RunWithoutDebuggingPackage.cs" company="BhaaL">
//     Copyright (c) BhaaL.  All rights reserved.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
        private BindingExpression _badgeBindingExpression;
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
            if (_badgeBindingExpression != null)
                Application.Current.Dispatcher.InvokeAsync(_badgeBindingExpression.UpdateTarget, DispatcherPriority.DataBind);
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

            // Visual Studio 2019 only: the title bar has a badge thingy with the solution name. we want to append the branch name there.
            UpdateVisualStudio2019SolutionBadge();

            // Since this package might not be initialized until after a solution has finished loading,
            // we need to check if a solution has already been loaded and then handle it.
            bool isSolutionLoaded = await IsSolutionLoadedAsync();
            if (isSolutionLoaded)
                SolutionOpened();
        }

        private void UpdateVisualStudio2019SolutionBadge()
        {
            // Visual Studio 2019 has an (internal) control named SolutionInfoControl which has a text binding in there.
            // We'll use this as a marker to see if we even have to do the control searching or not.
            var solutionInfoControlType = Type.GetType("Microsoft.VisualStudio.PlatformUI.SolutionInfoControl, Microsoft.VisualStudio.Shell.UI.Internal", false);
            if (solutionInfoControlType == null)
                return;
            // The type alone is not enough, it has a (default-)Style that should be there; if not it might be something else we should be looking at...
            if (!(Application.Current.MainWindow.TryFindResource(solutionInfoControlType) is Style))
                return;

            // the title badge contains the aforementioned SolutionInfoControl, but it is internal.
            // the next best type that isn't in some other assembly not referenced by this project is UserControl.
            var titleBarBadge = FindChild<UserControl>(Application.Current.MainWindow, "PART_SolutionNameTextBlock");
            if (titleBarBadge == null)
                return;

            // being an internal type, the dependency property is unreachable to us.
            // technically it isn't, but the base type TabItemTextControl is in an assembly that shouldn't be referenced directly for VS2017 compatibility.
            var textPropertyMember = titleBarBadge
                 .GetType()
                 .GetMember("TextProperty", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy)
                 .FirstOrDefault() as FieldInfo;
            if (!(textPropertyMember?.GetValue(null) is DependencyProperty textProperty))
                return;

            var textBindingExpression = titleBarBadge.GetBindingExpression(textProperty);
            if (textBindingExpression?.ParentBinding == null)
                return;

            var textBinding = textBindingExpression.ParentBinding;
            // duplicate the binding and insert our own converter to append the branch name
            var newTextBinding = new Binding
            {
                Converter = new SourceControlWindowTitleConverter(this, textBinding.Converter, TitleConverterPlacement.Back, TitleConverterSeparator.Pipe),
                Path = textBinding.Path,
            };

            titleBarBadge.SetBinding(textProperty, newTextBinding);
            // remember the binding expression so we can force an update when the branch name changes
            _badgeBindingExpression = titleBarBadge.GetBindingExpression(textProperty);
        }

        private static T FindChild<T>(DependencyObject parent, string childName) where T : DependencyObject
        {
            if (parent == null)
                return null;

            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (!(child is T))
                {
                    T foundChild = FindChild<T>(child, childName);

                    if (foundChild != null)
                        return foundChild;
                }
                else if (!string.IsNullOrEmpty(childName))
                {
                    if (child is FrameworkElement frameworkElement && frameworkElement.Name == childName)
                    {
                        return (T)child;
                    }
                }
                else
                {
                    return (T)child;
                }
            }

            return null;
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
