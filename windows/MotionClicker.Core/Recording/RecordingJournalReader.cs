using System.Text;
using static MotionClicker.Core.Recording.RecordingCheck;
namespace MotionClicker.Core.Recording;

internal static class RecordingJournalReader
{
    internal static void Read<T>(string path,Action<string> check,Action<T> visit) where T : class
    {
        check(path);
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        using var reader=new StreamReader(input,new UTF8Encoding(false,true));var buffer=new char[65536];var line=new StringBuilder();int length;
        while((length=reader.Read(buffer,0,buffer.Length))>0)for(var i=0;i<length;i++)
        {
            if(buffer[i]=='\n')
            {
                Require(line.Length>0,"Invalid journal line");visit(RecordingJson.Deserialize<T>(line.ToString()));line.Clear();
            }
            else{line.Append(buffer[i]);Require(line.Length<=1048576,"Oversized journal record");}
        }
        Require(line.Length==0,"Truncated event journal");
    }
}
