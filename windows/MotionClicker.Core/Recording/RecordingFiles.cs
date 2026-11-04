using System.Text.RegularExpressions;
using static MotionClicker.Core.Recording.RecordingCheck;
namespace MotionClicker.Core.Recording;

internal sealed class RecordingFiles
{
    internal string Root { get; }
    private readonly string root;
    internal RecordingFiles(string root)
    {
        var initial=Path.GetFullPath(root);var attrs=File.GetAttributes(initial);
        Require(attrs.HasFlag(FileAttributes.Directory)&&!attrs.HasFlag(FileAttributes.ReparsePoint),"Root must be an ordinary directory");
        this.root=CanonicalDirectory(initial);Root=this.root;
    }
    internal static string CanonicalDirectory(string path)
    {
        var info=new DirectoryInfo(path);if(info.Parent is null)return info.FullName;
        var parent=CanonicalDirectory(info.Parent.FullName);var candidate=Path.Combine(parent,info.Name);
        var current=new DirectoryInfo(candidate);
        return current.Attributes.HasFlag(FileAttributes.ReparsePoint)?current.ResolveLinkTarget(true)?.FullName??throw new RecordingException("Unresolved directory link"):candidate;
    }
    internal static void Id(string id)
    {
        var stem=id.Split('.')[0].ToUpperInvariant();
        Require(!id.EndsWith('.') && !Regex.IsMatch(stem,@"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z",RegexOptions.CultureInvariant),"Nonportable filesystem ID");
        Require(Regex.IsMatch(id,@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z",RegexOptions.CultureInvariant),"Invalid capture ID");
    }
    internal static bool Exists(string path)
    {
        try{_=File.GetAttributes(path);return true;}catch(FileNotFoundException){return false;}catch(DirectoryNotFoundException){return false;}
    }
    internal void Check(string path)
    {
        var relative=Path.GetRelativePath(root,Path.GetFullPath(path));
        Require(relative!=".."&&!relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&!Path.IsPathRooted(relative),"Path outside capture root");
        var cursor=root;var attrs=File.GetAttributes(cursor);Require(attrs.HasFlag(FileAttributes.Directory)&&!attrs.HasFlag(FileAttributes.ReparsePoint),"Root redirected");
        if(relative==".")return;
        foreach(var part in relative.Split(Path.DirectorySeparatorChar))
        {
            if(Directory.Exists(cursor))foreach(var child in Directory.EnumerateFileSystemEntries(cursor))
            {
                var name=Path.GetFileName(child);if(string.Equals(name,part,StringComparison.OrdinalIgnoreCase))Require(name==part,"Case-folded path collision");
            }
            cursor=Path.Combine(cursor,part);if(Exists(cursor))Require(!File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint),"Reparse point in capture path");
        }
    }
    internal void CheckTree(string path)
    {
        Check(path);
        if(File.GetAttributes(path).HasFlag(FileAttributes.Directory))
            foreach(var child in Directory.EnumerateFileSystemEntries(path))CheckTree(child);
    }
    internal void EnsureDirectory(string path)
    {
        Check(path);if(!Exists(path))Directory.CreateDirectory(path);Require(File.GetAttributes(path).HasFlag(FileAttributes.Directory),"Expected directory");
    }
}
