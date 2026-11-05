namespace SourceGit.Commands
{
    public class Restore : Command
    {
        public Restore(string repo, string pathspecFile, bool lockSubmoduleDiscard = true)
        {
            WorkingDirectory = repo;
            Context = repo;
            var recurse = lockSubmoduleDiscard ? "--no-recurse-submodules" : "--recurse-submodules";
            Args = $"restore --progress --worktree {recurse} --pathspec-from-file={pathspecFile.Quoted()}";
        }
    }
}
