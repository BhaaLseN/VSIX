using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace GitHub.BhaaLseN.VSIX.SourceControl
{
    public abstract class SourceControlWatcher : IDisposable
    {
        private static readonly ConstructorInfo[] _validSourceControlWatcherCtors;

        static SourceControlWatcher()
        {
            // grab all valid classes derived from ourselves, and see if they have a ctor
            // that takes exactly one string parameter (which is the solution directory).
            _validSourceControlWatcherCtors = typeof(SourceControlWatcher).Assembly
                .GetTypes()
                .Where(t => !t.IsAbstract)
                .Where(t => typeof(SourceControlWatcher).IsAssignableFrom(t))
                .Select(t => t.GetConstructor(new[] { typeof(string) }))
                .Where(c => c != null)
                .ToArray();
        }

        protected SourceControlWatcher(string solutionDirectory)
        {
            SolutionDirectory = solutionDirectory;
        }

        public static SourceControlWatcher Create(string solutionFilePath)
        {
            if (string.IsNullOrEmpty(solutionFilePath))
                return null;

            string solutionDirectory = Path.GetFullPath(Path.GetDirectoryName(solutionFilePath));
            if (!Directory.Exists(solutionDirectory))
                return null;

            // find and return the first valid SCM Watcher that know what this is.
            foreach (var ctor in _validSourceControlWatcherCtors)
            {
                var scw = (SourceControlWatcher)ctor.Invoke(new object[] { solutionDirectory });
                if (scw.IsValid)
                    return scw;
            }

            return null;
        }

        public string SolutionDirectory { get; }
        public abstract bool IsValid { get; }
        public string BranchName { get; private set; }

        public event EventHandler BranchNameChanged;
        protected void UpdateBranchName(string newBranchName)
        {
            if (BranchName == newBranchName)
                return;

            BranchName = newBranchName;
            BranchNameChanged?.Invoke(this, EventArgs.Empty);
        }

        #region IDisposable Support
        private bool _isDisposed;

        protected virtual void Dispose(bool disposing)
        {
            // no cleanup required here
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                Dispose(true);
                _isDisposed = true;
            }
        }
        #endregion
    }
}
