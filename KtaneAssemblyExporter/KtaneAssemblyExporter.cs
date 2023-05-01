using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Remoting;
using System.Threading;

public static class KtaneAssemblyExporter
{
    public enum ProgressStage
    {
        NotStarted,
        InProgress,
        Finished
    }

    public enum LogType
    {
        Error,
        Assert,
        Warning,
        Log,
        Exception
    }
    
    public class DomainProxy : MarshalByRefObject
    {
        public string CurrentTitle;
        public string CurrentAction;
        public ProgressStage Stage = ProgressStage.NotStarted;
        public float Progress;
        public Queue<Action> MainThreadDispatch = new Queue<Action>();
        public AppDomain ImporterDomain;
        public Action<string> HandleLog;
        public Action<LogType, string> HandleTypedLog;
        public List<string> ComponentTypes = new List<string>();
        public Exception Error;

        public void SetException(Exception e)
        {
            Error = e;
        }
        
        public void Finished()
        {
            Dispatch(() =>
            {
                AppDomain.Unload(ImporterDomain);
                Stage = ProgressStage.Finished;
            });
        }

        public void AddComponentType(string typeName)
        {
            lock(ComponentTypes)
                ComponentTypes.Add(typeName);
        }

        public void Log(string msg)
        {
            Dispatch(() => HandleLog(msg));
        }

        public void Log(LogType logType, string msg)
        {
            Dispatch(() => HandleTypedLog(logType, msg));
        }

        public void Dispatch(Action action)
        {
            lock(MainThreadDispatch)
                MainThreadDispatch.Enqueue(action);
        }
    }

    public class ExportHandler : MarshalByRefObject
    {
        private volatile DomainProxy Proxy;

        private class StartInfo
        {
            public string GameLibPath;
            public string OutputPath;
            public bool AddHideInInspector;
        }
        
        public void Init(ObjectHandle proxy)
        {
            Proxy = proxy.Unwrap() as DomainProxy;
        }

        public void Start(string gameLibPath, string outputPath, bool addHideInInspector)
        {
            var thread = new Thread(Handle)
            {
                IsBackground = true
            };
            thread.Start(new StartInfo
            {
                GameLibPath = gameLibPath,
                OutputPath = outputPath,
                AddHideInInspector = addHideInInspector
            });
        }

        public void Handle(object _startInfo)
        {
            try
            {
                var startInfo = (StartInfo)_startInfo;
                var assemblies = new List<Assembly>();
                var files = Directory.GetFiles(startInfo.GameLibPath, "*.dll");
                var delta = 1f / files.Length;
                Proxy.Progress = 0;
                foreach (var asmPath in files)
                {
                    var asmName = Path.GetFileNameWithoutExtension(asmPath);
                    Proxy.CurrentAction = $"Loading {asmName}";
                    var asm = Assembly.LoadFrom(asmPath);
                    if (asmName == "Assembly-CSharp" || asmName == "Assembly-CSharp-firstpass")
                        assemblies.Add(asm);
                    Proxy.Progress += delta;
                }

                KtaneAssemblyStripper.StripPath = startInfo.OutputPath;
                KtaneAssemblyStripper.BasePath = Path.GetDirectoryName(startInfo.OutputPath);

                for (var i = 0; i < assemblies.Count; i++)
                {
                    var asm = assemblies[i];
                    Proxy.CurrentTitle = $"Exporting {asm.GetName().Name} ({i + 2}/7)";
                    KtaneAssemblyStripper.StripAssembly(asm, startInfo.AddHideInInspector, Proxy);
                }
            }
            catch (Exception e)
            {
                Proxy.SetException(e);
            }
            finally
            {
                Proxy.Finished();
            }
        }
    }

    public static void StripAndExportAssemblies(DomainProxy proxy, string gameLibPath, string outputPath, bool addHideInInspector)
    {
        proxy.Stage = ProgressStage.InProgress;
        var domain = AppDomain.CreateDomain("KtaneExporterDomain");
        proxy.ImporterDomain = domain;
        var handler =
            domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location, typeof(ExportHandler).FullName)
                as ExportHandler;
        proxy.CurrentTitle = "Loading assemblies into appdomain (1/7)";
        handler.Init(new ObjectHandle(proxy));
        handler.Start(gameLibPath, outputPath, addHideInInspector);
    }
}