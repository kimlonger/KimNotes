using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using KimNotes;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
internal static class ImageResizeRegression
{
    [STAThread] static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("KIMNOTES_DATA_DIR",args[0]);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try {
            typeof(note).Assembly.GetType("KimNotes.utils.InitConfig").GetMethod("InitSettings",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public).Invoke(null,null);
            Action<bool,string> check=(ok,message)=>{if(!ok)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);};
            if(args.Length>1 && args[1]=="layout")typeof(ExperienceRegression).GetMethod("LayoutAndSave",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{args[0],check});
            else ExperienceRegression.RunImages(check);
            Console.WriteLine("Image resize regression passed: "+args[0]);return 0;
        } catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
