using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using ModkitEditorUtils;
using Newtonsoft.Json;
using DomainProxy = KtaneAssemblyExporter.DomainProxy;
using ProgressStage = KtaneAssemblyExporter.ProgressStage;

public class KtaneAssemblyImporterWindow : EditorWindow
{
    [MenuItem("Keep Talking ModKit/Import Assembly-CSharp", priority = 21)]
    static void OpenImporterWindow()
    {
        var window = GetWindow<KtaneAssemblyImporterWindow>("Import game assembly");
        window.position = new Rect(Screen.width / 2, Screen.height / 2, 500, 100);
        window.Show();
    }

    private static readonly Dictionary<string, object> ProxyAssemblyDefinition = new Dictionary<string, object>
    {
        { "name", "GameProxies" },
        { "references", new string[0] },
        { "includePlatforms", new string[0] },
        { "excludePlatforms", new string[0] }
    };

    public static Action UpdateAssemblyDefinitions;
    
    private DomainProxy ProxyInstance = new DomainProxy();

    private const string PathError = "Invalid KTaNE path";
    private string GamePath;
    private string GameLibrariesPath;
    private bool AddHideInInspector;

    private void ClearDispatchQueue()
    {
        lock (ProxyInstance.MainThreadDispatch)
        {
            while (ProxyInstance.MainThreadDispatch.Count > 0)
                ProxyInstance.MainThreadDispatch.Dequeue().Invoke();
        }
    }

    private string CheckGamePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return PathError;
        GameLibrariesPath = Path.Combine(path, "ktane_Data/Managed");
        if (!Directory.Exists(GameLibrariesPath) ||
            !File.Exists(Path.Combine(GameLibrariesPath, "Assembly-CSharp.dll")) ||
            !File.Exists(Path.Combine(GameLibrariesPath, "Assembly-CSharp-firstpass.dll")))
            return PathError;
        return path;
    }

    private void OnGUIHandler()
    {
        GUI.enabled = ProxyInstance.Stage == ProgressStage.NotStarted;
        ClearDispatchQueue();
        if (GUILayout.Button("Select game directory", GUILayout.Width(200)))
            GamePath = CheckGamePath(EditorUtility.OpenFolderPanel("Select game directory", "", ""));
        if (!string.IsNullOrEmpty(GamePath))
            GUILayout.Label(GamePath);
        GUILayout.Space(10);
        AddHideInInspector = EditorGUILayout.ToggleLeft("Add HideInInspector attribute to originally hidden fields",
            AddHideInInspector);
        GUI.enabled = ProxyInstance.Stage == ProgressStage.NotStarted && !string.IsNullOrEmpty(GamePath) &&
                      GamePath != PathError;
        if (GUILayout.Button("Import"))
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var StripPath = Path.Combine(projectRoot, "strip");
            ProxyInstance = new DomainProxy();
            ProxyInstance.HandleLog = Debug.Log;
            ProxyInstance.HandleTypedLog = (logType, msg) => Debug.unityLogger.Log((LogType)logType, msg);
            KtaneAssemblyExporter.StripAndExportAssemblies(ProxyInstance, GameLibrariesPath, StripPath,
                AddHideInInspector);
            while (ProxyInstance.Stage == ProgressStage.InProgress)
            {
                EditorUtility.DisplayProgressBar(ProxyInstance.CurrentTitle, ProxyInstance.CurrentAction,
                    ProxyInstance.Progress);
                Thread.Sleep(100);
                ClearDispatchQueue();
            }

            if (ProxyInstance.Error != null)
                throw ProxyInstance.Error;

            EditorUtility.DisplayProgressBar("Recompiling assemblies (4/7)", "Recompiling Assembly-CSharp-firstpass",
                0f);
            var scriptFiles = Directory.GetFiles(Path.Combine(StripPath, "Assembly-CSharp-firstpass"), "*.cs",
                SearchOption.AllDirectories);
            var destination = Path.Combine(Application.dataPath, "Plugins/Managed/GameAssemblies");
            if (!Directory.Exists(destination))
                Directory.CreateDirectory(destination);
            var output = Path.Combine(StripPath, "Assembly-CSharp-firstpass.dll");
            var gameLibraryReferences = Directory.GetFiles(GameLibrariesPath, "*.dll").Where(p =>
            {
                var fName = Path.GetFileNameWithoutExtension(p);
                return !fName.StartsWith("UnityEngine") && !fName.StartsWith("Assembly-CSharp") &&
                       !fName.StartsWith("System") && fName != "mscorlib";
            }).ToArray();
            var managedReferences = new[]
                {
                    "Managed/UnityEngine",
                    "UnityExtensions/Unity/GUISystem/UnityEngine.UI"
                }.Select(p => Path.Combine(EditorApplication.applicationContentsPath, p))
                .Concat(gameLibraryReferences.Select(p => Path.ChangeExtension(p, null))).ToList();
            var success = ModkitCompiler.CompileAssembly(scriptFiles, managedReferences.ToArray(), new string[0], output, false);
            if (!success)
                throw new ApplicationException("Failed to recompile Assembly-CSharp-firstpass");
            var firstpassLocation = Path.Combine(StripPath, "Assembly-CSharp-firstpass-forwarder.dll");
            File.Copy(output, firstpassLocation, true);
            EditorUtility.DisplayProgressBar("Recompiling assemblies (4/7)", "Recompiling Assembly-CSharp", .5f);
            managedReferences.Add(firstpassLocation);  //Add the compiled Assembly-CSharp-firstpass as reference to Assembly-CSharp
            scriptFiles = Directory.GetFiles(Path.Combine(StripPath, "Assembly-CSharp"), "*.cs",
                SearchOption.AllDirectories);
            output = Path.Combine(StripPath, "Assembly-CSharp.dll");
            success = ModkitCompiler.CompileAssembly(scriptFiles, managedReferences.ToArray(), new string[0], output, false);
            if (!success)
                throw new ApplicationException("Failed to recompile Assembly-CSharp");
            File.Copy(firstpassLocation,
                Path.Combine(destination, "Assembly-CSharp-firstpass-forwarder.dll"), true);
            File.Copy(Path.Combine(StripPath, "Assembly-CSharp.dll"),
                Path.Combine(destination, "Assembly-CSharp-forwarder.dll"), true);
            var progress = 0f;
            var delta = 1f / (gameLibraryReferences.Length - 3);
            foreach (var gameLib in gameLibraryReferences)
            {
                var libName = Path.GetFileName(gameLib);
                if(libName == "Newtonsoft.Json.dll" || libName == "KMFramework.dll" || libName.StartsWith("Mono."))
                    continue;
                EditorUtility.DisplayProgressBar("Copying assemblies (5/7)", $"Copying {libName}", progress);
                File.Copy(gameLib, Path.Combine(destination, libName), true);
                progress += delta;
            }

            EditorUtility.DisplayProgressBar("Generating proxy scripts (6/7)", "Setting up proxy assembly", 0f);
            var proxyLocation = Path.Combine(Application.dataPath, "Scripts/GameProxies");
            if (!Directory.Exists(proxyLocation))
                Directory.CreateDirectory(proxyLocation);
            File.WriteAllText(Path.Combine(Application.dataPath, "Scripts/GameProxies/GameProxies.asmdef"),
                JsonConvert.SerializeObject(ProxyAssemblyDefinition, Formatting.Indented));
            progress = 0f;
            delta = 1f / ProxyInstance.ComponentTypes.Count;
            File.WriteAllLines(Path.Combine(proxyLocation, "_ImportChecker.cs"), new[]
            {
                "#if !GAME_ASSEMBLIES",
                "#error This project uses in-game types, but the game assemblies haven't been imported. Import them via the `Keep Talking ModKit > Import Assembly-CSharp` menu.",
                "#endif"
            });
            foreach (var fullTypeName in ProxyInstance.ComponentTypes)
            {
                var splitted = fullTypeName.Split('.');
                var typeName = splitted[splitted.Length - 1];
                EditorUtility.DisplayProgressBar("Generating proxy scripts (6/7)", typeName, progress);
                File.WriteAllLines(Path.Combine(proxyLocation, typeName + "Proxy.cs"), new[]
                {
                    "#if GAME_ASSEMBLIES",
                    "#pragma warning disable 114",
                    $"[UnityEngine.AddComponentMenu(\"KTaNE/{typeName}\")]",
                    $"public class {typeName}Proxy : {fullTypeName}",
                    "{",
                    "}",
                    "#endif"
                }, Encoding.UTF8);
                progress += delta;
            }

            UpdateAssemblyDefinitions?.Invoke();
            
            CleanUp(false);
        }

        GUI.enabled = true;
    }

    private void CleanUp(bool error)
    {
        EditorUtility.DisplayProgressBar("Cleaning up (7/7)", "Cleaning up", 1f);
        var stripPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "strip");
        if(Directory.Exists(stripPath))
            Directory.Delete(stripPath, true);
        if (error)
        {
            var gameAssembliesPath = Path.Combine(Application.dataPath, "Plugins/Managed/GameAssemblies");
            if(Directory.Exists(gameAssembliesPath))
                Directory.Delete(gameAssembliesPath, true);
            var proxiesPath = Path.Combine(Application.dataPath, "Scripts/GameAssemblies");
            if (Directory.Exists(proxiesPath))
                Directory.Delete(proxiesPath, true);
        }
        else Debug.Log("KTaNE libraries imported successfully!");
        EditorUtility.ClearProgressBar();
        ProxyInstance.Stage = ProgressStage.NotStarted;
        Close();
    }

    private void OnGUI()
    {
        try
        {
            OnGUIHandler();
        }
        catch (Exception e)
        {
            Debug.LogError("An error occurred while importing KTaNE libraries");
            Debug.LogException(e);
            CleanUp(true);
        }
    }
}
