using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;

namespace GitHub.BhaaLseN.VSIX
{
    public class SolutionEventListener : IVsSolutionEvents, IDisposable
    {
        private uint _solutionEventsCookie;
        private IVsSolution _solution;

        public event Action AfterSolutionLoaded;

        public SolutionEventListener(IVsSolution solution)
        {
            _solution = solution;
            _solution?.AdviseSolutionEvents(this, out _solutionEventsCookie);
        }

        int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
        int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
        int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
        int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
        int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
        int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;

        int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            AfterSolutionLoaded?.Invoke();
            return VSConstants.S_OK;
        }

        int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
        int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;
        int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        public void Dispose()
        {
            if (_solution == null || _solutionEventsCookie == 0)
                return;

            GC.SuppressFinalize(this);
            _solution.UnadviseSolutionEvents(_solutionEventsCookie);
            _solution = null;
            _solutionEventsCookie = 0;
            AfterSolutionLoaded = null;
        }
    }
}
