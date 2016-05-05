using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace GitHub.BhaaLseN.VSIX.SourceControl
{
    public class GitWatcher : SourceControlWatcher
    {
        private readonly string _gitDirectory;
        private readonly string _gitHead;
        private readonly FileSystemWatcher _headWatcher;

        public GitWatcher(string solutionDirectory)
            : base(solutionDirectory)
        {
            _gitDirectory = FindDotGitDirectory(solutionDirectory);
            IsValid = !string.IsNullOrEmpty(_gitDirectory) && Directory.Exists(_gitDirectory);
            if (IsValid)
            {
                _gitHead = Path.Combine(_gitDirectory, "HEAD");
                _headWatcher = new FileSystemWatcher(_gitDirectory, "HEAD")
                {
                    IncludeSubdirectories = false,
                };
                _headWatcher.Changed += HEAD_Changed;
                _headWatcher.EnableRaisingEvents = true;
                SyncBranchName();
            }
        }

        private void HEAD_Changed(object sender, FileSystemEventArgs e)
        {
            if (e.Name == "HEAD")
                SyncBranchName();
        }

        private static string FindDotGitDirectory(string solutionDirectory)
        {
            if (string.IsNullOrEmpty(solutionDirectory))
                return null;

            var directory = new DirectoryInfo(solutionDirectory);
            while (directory.Exists && directory.Parent != null)
            {
                // every .git directory has a HEAD...at least it should have.
                string gitHead = Path.Combine(directory.FullName, ".git", "HEAD");
                if (File.Exists(gitHead))
                    return Path.GetDirectoryName(gitHead);

                directory = directory.Parent;
            }

            return null;
        }

        // refspec for a local branch (resides in the refs/heads/ namespace)
        private static readonly Regex LocalBranchRef = new Regex(@"refs/(?:heads/|remotes/)?(?<name>.+)$", RegexOptions.Compiled | RegexOptions.Singleline);
        private void SyncBranchName()
        {
            string head = ReadHeadRef();

            // check if we got a refspec first...
            var match = LocalBranchRef.Match(head);
            if (match.Success)
            {
                UpdateBranchName(match.Groups["name"].Value);
                return;
            }

            // ...then check for a hash that can be resolved (most likely a detached head)
            if (head.Length == 40)
            {
                string firstMatchingFile = Directory.GetFiles(Path.Combine(_gitDirectory, "refs"), "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => FileMatchesHash(f, head));
                if (!string.IsNullOrEmpty(firstMatchingFile))
                {
                    // if theres a match, grab the file name and build a refspec-ish name from it
                    string refspec = firstMatchingFile
                        .Replace('\\', '/')
                        .Substring((_gitDirectory + "refs/").Length)
                        .TrimStart('/');
                    // strip local heads/ and remotes/ namespaces, but leave the remote name and tags/
                    if (refspec.StartsWith("heads/"))
                        refspec = refspec.Substring("heads/".Length);
                    if (refspec.StartsWith("remotes/"))
                        refspec = refspec.Substring("remotes/".Length);
                    // include braces to indicate we're detached
                    UpdateBranchName(string.Format("({0})", refspec));
                    return;
                }
            }

            // if everything else fails, shorten the spec
            UpdateBranchName(string.Format("({0}...)", head.Substring(0, Math.Min(10, head.Length))));
        }

        private static bool FileMatchesHash(string filePath, string refspec)
        {
            string fileRefHash = ReadGitFile(filePath);
            return string.Equals(refspec, fileRefHash, StringComparison.InvariantCultureIgnoreCase);
        }
        private string ReadHeadRef()
        {
            return ReadGitFile(_gitHead);
        }

        private static string ReadGitFile(string gitFile)
        {
            int tries = 5;
            while (tries --> 0)
            {
                // try to read the file reference from Git.
                // Might fail when Git is updating the file at the same time, so sleep and retry a couple of times.
                try
                {
                    return File.ReadAllText(gitFile).Trim();
                }
                catch
                {
                    Thread.Sleep(10);
                }
            }
            return null;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _headWatcher.EnableRaisingEvents = false;
                _headWatcher.Dispose();
            }
        }

        public override bool IsValid { get; }
    }
}
