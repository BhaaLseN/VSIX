using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GitHub.BhaaLseN.VSIX.SourceControl
{
    public class GitWatcher : SourceControlWatcher
    {
        private readonly string _gitDirectory;
        private readonly string _gitHead;
        private FileSystemWatcher _headWatcher;
        private TaskCompletionSource<bool> _continueGoing = new TaskCompletionSource<bool>();

        public GitWatcher(string solutionDirectory)
            : base(solutionDirectory)
        {
            _gitDirectory = FindDotGitDirectory(solutionDirectory);
            IsValid = !string.IsNullOrEmpty(_gitDirectory) && Directory.Exists(_gitDirectory);
            if (IsValid)
            {
                _gitHead = Path.Combine(_gitDirectory, "HEAD");
                ResetWatcher();
                SyncBranchName();
                MonitorHeadChanges();
            }
        }

        private Task<bool> TheNextHeadChange()
        {
            return _continueGoing.Task;
        }
        private async void MonitorHeadChanges()
        {
            while (await TheNextHeadChange().ConfigureAwait(false))
            {
                SyncBranchName();
            }
        }
        private void NotifyHeadHasChanged()
        {
            var oldResult = _continueGoing;
            _continueGoing = new TaskCompletionSource<bool>();
            oldResult.SetResult(true);
        }

        private void ResetWatcher()
        {
            var oldWatcher = _headWatcher;
            if (oldWatcher != null)
            {
                oldWatcher.EnableRaisingEvents = false;
                oldWatcher.Error -= Watcher_Error;
                oldWatcher.Changed -= HEAD_Changed;
                oldWatcher.Dispose();
            }

            var newWatcher = new FileSystemWatcher(_gitDirectory, "HEAD")
            {
                IncludeSubdirectories = false,
            };
            newWatcher.Changed += HEAD_Changed;
            newWatcher.Error += Watcher_Error;
            newWatcher.EnableRaisingEvents = true;
            _headWatcher = newWatcher;
        }

        private void Watcher_Error(object sender, ErrorEventArgs e)
        {
            // we can't be certain what happened and react to it accordingly; so we just
            // go and reset the watcher altogether.
            ResetWatcher();
            // also try to re-sync the branch name since we might've been caught somewhere
            // in the middle of an update.
            SyncBranchName();
        }

        private void HEAD_Changed(object sender, FileSystemEventArgs e)
        {
            if (e.Name == "HEAD")
                NotifyHeadHasChanged();
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
                // try the packed-refs first, when they're there; then look for unpacked refs last.
                string packedRefsFile = Path.Combine(_gitDirectory, "packed-refs");
                if (File.Exists(packedRefsFile))
                {
                    // grab all lines from the packed-refs file, and discard all that don't look like
                    // a comment, a discarded ref or anything else that couldn't be one.
                    var packedRefs = File.ReadAllLines(packedRefsFile)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Where(l => l[0] != '^' && l[0] != '#' && l.Length > 42 && l.Contains(' '))
                        .Select(l => l.Split(new[] { ' ' }, 2))
                        .ToLookup(k => k[0], v => v[1]);

                    var matchingRefs = packedRefs[head];
                    if (matchingRefs.Any())
                    {
                        // we got at least one match; pick the shortest one that is a local branch,
                        // the shortest one that is a remote branch or the shortest one that is a tag.
                        string pickedRef = matchingRefs
                            .OrderBy(r => r.StartsWith("refs/heads/"))
                            .ThenBy(r => r.StartsWith("refs/remotes/"))
                            .ThenBy(r => r.StartsWith("refs/tags/"))
                            .ThenBy(r => r.Length)
                            .First();

                        // drop the "refs/" prefix
                        pickedRef = pickedRef.Substring("refs/".Length);
                        if (pickedRef.StartsWith("heads/"))
                        {
                            pickedRef = pickedRef.Substring("heads/".Length);
                        }
                        else
                        {
                            // this is most likely a detached ref; add brackets.
                            if (pickedRef.StartsWith("remotes/"))
                                pickedRef = pickedRef.Substring("remotes/".Length);
                            pickedRef = '(' + pickedRef + ')';
                        }

                        UpdateBranchName(pickedRef);
                        return;
                    }
                }

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
                _continueGoing.SetResult(false);
                _headWatcher.EnableRaisingEvents = false;
                _headWatcher.Dispose();
            }
        }

        public override bool IsValid { get; }
    }
}
