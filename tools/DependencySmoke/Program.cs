using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using LiveInteract.Bootstrap;

namespace DependencySmoke
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            try
            {
                var engine=new DependencyEngine(root,root,(s,n)=>Console.WriteLine(s));
                Check(engine.Catalog.Packages.Length==3,"runtime catalog");
                foreach(var bad in new[]{"http://builds.dotnet.microsoft.com/a","https://microsoft.com.evil.invalid/a","https://user@builds.dotnet.microsoft.com/a"})
                { var denied=false;try{DependencyEngine.TrustedUri(bad);}catch(InvalidDataException){denied=true;}Check(denied,"untrusted URI accepted"); }
                var fake=Path.Combine(root,"fake-runtime");Directory.CreateDirectory(fake);File.WriteAllText(Path.Combine(fake,"dotnet.exe"),"fake");
                foreach(var f in new[]{"Microsoft.NETCore.App","Microsoft.WindowsDesktop.App","Microsoft.AspNetCore.App"})Directory.CreateDirectory(Path.Combine(fake,"shared",f,"10.0.13"));
                Check(!DependencyEngine.HasRuntime(fake),"empty runtime accepted");
                var zip=Path.Combine(root,"unsafe.zip");using(var z=ZipFile.Open(zip,ZipArchiveMode.Create))using(var w=new StreamWriter(z.CreateEntry("../escape.txt").Open()))w.Write("must not escape");
                var rejected=false;try{DependencyEngine.ExtractChecked(zip,Path.Combine(root,"extract"));}catch(InvalidDataException){rejected=true;}
                Check(rejected&&!File.Exists(Path.Combine(root,"escape.txt")),"zip traversal");
                var unsigned=false;try{DependencyEngine.VerifyMicrosoft(Assembly.GetExecutingAssembly().Location);}catch(InvalidDataException){unsigned=true;}
                Check(unsigned,"unsigned installer accepted");
                if(args.Length>1)
                {
                    var file=Path.Combine(root,"MicrosoftEdgeWebview2Setup.exe");
                    DependencyEngine.Download(engine.Catalog.WebViewUrl,file,null,CancellationToken.None,n=>{}).GetAwaiter().GetResult();
                    DependencyEngine.VerifyMicrosoft(file);Console.WriteLine("Official WebView installer downloaded and signature verified; NOT executed");
                }
                File.WriteAllText(Path.Combine(root,"dependency-smoke.json"),"{\"Passed\":true,\"NoBrowserOpened\":true,\"NoSystemInstallerExecuted\":true}");
                Console.WriteLine("DependencySmoke PASS");return 0;
            }
            catch(Exception error){File.WriteAllText(Path.Combine(root,"error.txt"),error.ToString());Console.WriteLine(error);return 1;}
        }
        private static void Check(bool value,string text){if(!value)throw new Exception(text);}
    }
}
