using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

#if !SELF_TEST
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using MelonLoader;
#endif

namespace OtogiCgUnlock
{
    internal enum SceneResponseAction
    {
        Original,
        Substitute,
        Queue
    }

    internal sealed class UnlockConfig
    {
        internal string Root = UnlockLogic.DefaultRemoteRoot;

        internal bool RemoteEnabled
        {
            get { return !string.IsNullOrWhiteSpace(Root); }
        }
    }

    internal static class UnlockLogic
    {
        internal const string DefaultRemoteRoot =
            "https://raw.githubusercontent.com/ImoutoHeaven/otogi-scenes/refs/heads/main";

        internal const string DefaultConfigText =
            "[Remote]\n" +
            "Root = https://raw.githubusercontent.com/ImoutoHeaven/otogi-scenes/refs/heads/main\n";

        internal static UnlockConfig LoadConfig(string path)
        {
            if (!File.Exists(path))
                File.WriteAllText(path, DefaultConfigText, new UTF8Encoding(false));
            return ParseConfig(File.ReadAllLines(path, Encoding.UTF8));
        }

        internal static UnlockConfig ParseConfig(IEnumerable<string> lines)
        {
            var result = new UnlockConfig();
            var section = string.Empty;
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                var equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;
                var key = (section + "." + line.Substring(0, equals).Trim())
                    .ToLowerInvariant();
                var value = line.Substring(equals + 1).Trim();
                if (key == "remote.root")
                    result.Root = value;
            }
            return result;
        }

        internal static string JoinRemoteUrl(string root, string relative)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(relative))
                return null;
            return root.TrimEnd('/') + "/" + relative.TrimStart('/');
        }

        internal static string CacheRelative(string folder, string id)
        {
            return folder + "/" + id + ".json";
        }

        internal static bool TryParseSceneUrl(string url, out string folder, out string id)
        {
            folder = null;
            id = null;
            Uri uri;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri))
                return false;
            var path = uri.AbsolutePath.TrimEnd('/');
            if (TryMatch(path, "/api/MAdults/MonsterMAdults/", out id))
            {
                folder = "adults";
                return true;
            }
            if (TryMatch(path, "/api/MScenes/", out id))
            {
                folder = "scenes";
                return true;
            }
            if (TryMatch(path, "/api/episode/monsters/", out id) ||
                TryMatch(path, "/api/episode/spirits/", out id))
            {
                folder = "episodes";
                return true;
            }
            return false;
        }

        internal static SceneResponseAction DecideSceneResponse(
            string url,
            int originalStatus,
            bool fileExists,
            bool remoteEnabled)
        {
            string folder;
            string id;
            if (!TryParseSceneUrl(url, out folder, out id))
                return SceneResponseAction.Original;
            if (originalStatus != 400 && originalStatus != 404)
                return SceneResponseAction.Original;
            if (fileExists)
                return SceneResponseAction.Substitute;
            return remoteEnabled
                ? SceneResponseAction.Queue
                : SceneResponseAction.Original;
        }

        internal static string[] ExtractPrefetchPaths(string json)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            CollectPrefetchPaths(JToken.Parse(json), result, seen);
            return result.ToArray();
        }

        private static void CollectPrefetchPaths(
            JToken token, List<string> result, HashSet<string> seen)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                AddPrefetchId(obj["MAdultId"], "adults", result, seen);
                AddPrefetchId(obj["MSceneId"], "scenes", result, seen);
                foreach (var property in obj.Properties())
                    CollectPrefetchPaths(property.Value, result, seen);
                return;
            }

            var array = token as JArray;
            if (array == null)
                return;
            foreach (var child in array)
                CollectPrefetchPaths(child, result, seen);
        }

        private static void AddPrefetchId(
            JToken token, string folder, List<string> result, HashSet<string> seen)
        {
            if (token == null || token.Type == JTokenType.Null)
                return;
            int id;
            if (token.Type == JTokenType.Integer)
                id = token.Value<int>();
            else if (token.Type == JTokenType.String)
            {
                if (!int.TryParse(
                    (string)token, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                    return;
            }
            else
                return;
            if (id == 0)
                return;
            var relative = CacheRelative(folder, id.ToString(CultureInfo.InvariantCulture));
            if (seen.Add(relative))
                result.Add(relative);
        }

        private static bool TryMatch(string path, string marker, out string id)
        {
            id = null;
            var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;
            var candidate = path.Substring(index + marker.Length);
            if (candidate.Length == 0 || candidate.Length > 10 || candidate.IndexOf('/') >= 0)
                return false;
            for (var i = 0; i < candidate.Length; i++)
            {
                if (candidate[i] < '0' || candidate[i] > '9')
                    return false;
            }
            id = candidate;
            return true;
        }
    }

#if SELF_TEST
    public static class UnlockSelfTest
    {
        public static int Main()
        {
            const string defaultRoot =
                "https://raw.githubusercontent.com/ImoutoHeaven/otogi-scenes/refs/heads/main";
            const string adultUrl =
                "https://game.test/api/MAdults/MonsterMAdults/210013";
            const string episodeJson =
                "{\"Episodes\":[{\"MAdultId\":210013,\"MSceneId\":10001}," +
                "{\"MAdultId\":0,\"MSceneId\":10002}]}";

            var missing = UnlockLogic.ParseConfig(new string[0]);
            if (missing.Root != defaultRoot || !missing.RemoteEnabled)
                throw new InvalidOperationException("default cfg root self-check failed");

            var empty = UnlockLogic.ParseConfig(new[] { "[Remote]", "Root =" });
            if (empty.Root != "" || empty.RemoteEnabled)
                throw new InvalidOperationException("empty root self-check failed");

            var custom = UnlockLogic.ParseConfig(new[]
            {
                "[Remote]", "Root = https://example.test/scenes/"
            });
            if (custom.Root != "https://example.test/scenes/")
                throw new InvalidOperationException("custom root self-check failed");

            if (UnlockLogic.JoinRemoteUrl(defaultRoot, "adults/210013.json") !=
                    defaultRoot + "/adults/210013.json" ||
                UnlockLogic.JoinRemoteUrl(defaultRoot + "/", "characters.json") !=
                    defaultRoot + "/characters.json")
                throw new InvalidOperationException("remote url join self-check failed");

            string folder;
            string id;
            if (!UnlockLogic.TryParseSceneUrl(adultUrl, out folder, out id) ||
                folder != "adults" || id != "210013" ||
                UnlockLogic.CacheRelative(folder, id) != "adults/210013.json")
                throw new InvalidOperationException("adult path self-check failed");

            if (UnlockLogic.DecideSceneResponse(adultUrl, 400, true, true) !=
                    SceneResponseAction.Substitute ||
                UnlockLogic.DecideSceneResponse(adultUrl, 404, true, false) !=
                    SceneResponseAction.Substitute ||
                UnlockLogic.DecideSceneResponse(adultUrl, 200, true, true) !=
                    SceneResponseAction.Original ||
                UnlockLogic.DecideSceneResponse(adultUrl, 400, false, true) !=
                    SceneResponseAction.Queue ||
                UnlockLogic.DecideSceneResponse(adultUrl, 400, false, false) !=
                    SceneResponseAction.Original ||
                UnlockLogic.DecideSceneResponse(
                    "https://game.test/api/Episode/CharacterStory", 400, true, true) !=
                    SceneResponseAction.Original)
                throw new InvalidOperationException("substitute decision self-check failed");

            var paths = UnlockLogic.ExtractPrefetchPaths(episodeJson);
            if (paths.Length != 3 ||
                Array.IndexOf(paths, "adults/210013.json") < 0 ||
                Array.IndexOf(paths, "scenes/10001.json") < 0 ||
                Array.IndexOf(paths, "scenes/10002.json") < 0)
                throw new InvalidOperationException("prefetch extraction self-check failed");

            var directory = Path.Combine(
                Path.GetTempPath(), "otogi-cgunlock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "OtogiCgUnlock.cfg");
                var loaded = UnlockLogic.LoadConfig(path);
                if (loaded.Root != defaultRoot || !File.Exists(path) ||
                    File.ReadAllText(path, Encoding.UTF8).IndexOf(defaultRoot, StringComparison.Ordinal) < 0)
                    throw new InvalidOperationException("missing cfg write self-check failed");
                File.WriteAllText(path, "[Remote]\nRoot =\n", new UTF8Encoding(false));
                if (UnlockLogic.LoadConfig(path).RemoteEnabled)
                    throw new InvalidOperationException("written empty root self-check failed");
            }
            finally
            {
                Directory.Delete(directory, true);
            }

            Console.WriteLine("PASS unlock self-check");
            return 0;
        }
    }
#else
    internal static class OtogiCgUnlockRuntime
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DomainGet();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DomainAssemblyOpen(
            IntPtr domain, [MarshalAs(UnmanagedType.LPStr)] string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr AssemblyGetImage(IntPtr assembly);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ClassFromName(
            IntPtr image,
            [MarshalAs(UnmanagedType.LPStr)] string namespaze,
            [MarshalAs(UnmanagedType.LPStr)] string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ClassGetMethodFromName(
            IntPtr klass, [MarshalAs(UnmanagedType.LPStr)] string name, int argumentCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ClassGetParent(IntPtr klass);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ObjectGetClass(IntPtr instance);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ClassGetFieldFromName(
            IntPtr klass, [MarshalAs(UnmanagedType.LPStr)] string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FieldGetValue(IntPtr instance, IntPtr field, IntPtr output);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr RuntimeInvoke(
            IntPtr method, IntPtr instance, IntPtr parameters, ref IntPtr exception);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int StringLength(IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr StringChars(IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr StringNewUtf16(IntPtr value, int length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ObjectUnbox(IntPtr instance);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint GcHandleNew(
            IntPtr instance, [MarshalAs(UnmanagedType.I1)] bool pinned);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GcHandleGetTarget(uint handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GcHandleFree(uint handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int StatusCodeGetter(IntPtr instance, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void StatusCodeSetter(IntPtr instance, int value, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool IsSuccessGetter(IntPtr instance, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EpisodeSetupCell(
            IntPtr instance, IntPtr episodeViewModel, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CharacterStoryTransition(
            IntPtr instance, int isAdult, IntPtr episodeViewModel, IntPtr methodInfo);

        private static readonly object QueueLock = new object();
        private static readonly Queue<string> Downloads = new Queue<string>();
        private static readonly HashSet<string> Pending =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Unavailable =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> Attempts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static ClassGetMethodFromName _classGetMethod;
        private static ClassGetParent _classGetParent;
        private static ObjectGetClass _objectGetClass;
        private static ClassGetFieldFromName _classGetField;
        private static FieldGetValue _fieldGetValue;
        private static RuntimeInvoke _runtimeInvoke;
        private static StringLength _stringLength;
        private static StringChars _stringChars;
        private static StringNewUtf16 _stringNewUtf16;
        private static ObjectUnbox _objectUnbox;
        private static GcHandleNew _gcHandleNew;
        private static GcHandleGetTarget _gcHandleGetTarget;
        private static GcHandleFree _gcHandleFree;

        private static StatusCodeGetter _originalStatusCode;
        private static StatusCodeGetter _statusCodeDetour;
        private static IntPtr _statusCodeSlot;
        private static StatusCodeSetter _originalSetStatusCode;
        private static StatusCodeSetter _setStatusCodeDetour;
        private static IntPtr _setStatusCodeSlot;
        private static IsSuccessGetter _originalIsSuccess;
        private static IsSuccessGetter _isSuccessDetour;
        private static IntPtr _isSuccessSlot;
        private static EpisodeSetupCell _originalSetupCell;
        private static EpisodeSetupCell _setupCellDetour;
        private static IntPtr _setupCellSlot;
        private static CharacterStoryTransition _originalTransition;
        private static CharacterStoryTransition _transitionDetour;
        private static IntPtr _transitionSlot;

        private static IntPtr _webRequestGet;
        private static IntPtr _webRequestSend;
        private static IntPtr _webRequestIsDone;
        private static IntPtr _webRequestResponseCode;
        private static IntPtr _webRequestDownloadHandler;
        private static IntPtr _webRequestAbort;
        private static IntPtr _webRequestDispose;
        private static IntPtr _downloadHandlerText;

        private static UnlockConfig _config;
        private static uint _activeRequestHandle;
        private static long _activeRequestStarted;
        private static string _activeRelative;
        private static int _activeAttempt;
        private static long _nextRequest;
        private static int _subLogged;
        private static int _hookErrorLogged;
        private static int _prefetchErrorLogged;

        internal static void Install()
        {
            try
            {
                var il2cpp = NativeLibrary.Load("libil2cpp.so");
                var domainGet = (DomainGet)il2cpp.GetExport(typeof(DomainGet), "il2cpp_domain_get");
                var assemblyOpen = (DomainAssemblyOpen)il2cpp.GetExport(
                    typeof(DomainAssemblyOpen), "il2cpp_domain_assembly_open");
                var assemblyGetImage = (AssemblyGetImage)il2cpp.GetExport(
                    typeof(AssemblyGetImage), "il2cpp_assembly_get_image");
                var classFromName = (ClassFromName)il2cpp.GetExport(
                    typeof(ClassFromName), "il2cpp_class_from_name");
                _classGetMethod = (ClassGetMethodFromName)il2cpp.GetExport(
                    typeof(ClassGetMethodFromName), "il2cpp_class_get_method_from_name");
                _classGetParent = (ClassGetParent)il2cpp.GetExport(
                    typeof(ClassGetParent), "il2cpp_class_get_parent");
                _objectGetClass = (ObjectGetClass)il2cpp.GetExport(
                    typeof(ObjectGetClass), "il2cpp_object_get_class");
                _classGetField = (ClassGetFieldFromName)il2cpp.GetExport(
                    typeof(ClassGetFieldFromName), "il2cpp_class_get_field_from_name");
                _fieldGetValue = (FieldGetValue)il2cpp.GetExport(
                    typeof(FieldGetValue), "il2cpp_field_get_value");
                _runtimeInvoke = (RuntimeInvoke)il2cpp.GetExport(
                    typeof(RuntimeInvoke), "il2cpp_runtime_invoke");
                _stringLength = (StringLength)il2cpp.GetExport(
                    typeof(StringLength), "il2cpp_string_length");
                _stringChars = (StringChars)il2cpp.GetExport(
                    typeof(StringChars), "il2cpp_string_chars");
                _stringNewUtf16 = (StringNewUtf16)il2cpp.GetExport(
                    typeof(StringNewUtf16), "il2cpp_string_new_utf16");
                _objectUnbox = (ObjectUnbox)il2cpp.GetExport(
                    typeof(ObjectUnbox), "il2cpp_object_unbox");
                _gcHandleNew = (GcHandleNew)il2cpp.GetExport(
                    typeof(GcHandleNew), "il2cpp_gchandle_new");
                _gcHandleGetTarget = (GcHandleGetTarget)il2cpp.GetExport(
                    typeof(GcHandleGetTarget), "il2cpp_gchandle_get_target");
                _gcHandleFree = (GcHandleFree)il2cpp.GetExport(
                    typeof(GcHandleFree), "il2cpp_gchandle_free");

                var domain = domainGet();
                var firstpass = assemblyGetImage(assemblyOpen(domain, "Assembly-CSharp-firstpass"));
                var game = assemblyGetImage(assemblyOpen(domain, "Assembly-CSharp"));
                var http = classFromName(firstpass, "BestHTTP", "HTTPResponse");
                if (http == IntPtr.Zero)
                    throw new InvalidOperationException("HTTPResponse missing");

                _statusCodeDetour = GetStatusCode;
                Attach(FindMethod(http, "get_StatusCode", 0), _statusCodeDetour,
                    out _originalStatusCode, out _statusCodeSlot);
                _setStatusCodeDetour = SetStatusCode;
                Attach(FindMethod(http, "set_StatusCode", 1), _setStatusCodeDetour,
                    out _originalSetStatusCode, out _setStatusCodeSlot);
                _isSuccessDetour = GetIsSuccess;
                Attach(FindMethod(http, "get_IsSuccess", 0), _isSuccessDetour,
                    out _originalIsSuccess, out _isSuccessSlot);

                var cell = classFromName(game, "Otogi", "EpisodeScenarioCell");
                _setupCellDetour = BypassEpisodeGate;
                Attach(FindMethod(cell, "SetupCell", 1), _setupCellDetour,
                    out _originalSetupCell, out _setupCellSlot);

                var scene = classFromName(game, "Otogi", "CharacterStoryScene");
                _transitionDetour = MaybeForceAdult;
                Attach(FindMethod(scene, "SceneTransitionAsObservable", 2), _transitionDetour,
                    out _originalTransition, out _transitionSlot);

                if (_statusCodeSlot == IntPtr.Zero ||
                    _setStatusCodeSlot == IntPtr.Zero || _isSuccessSlot == IntPtr.Zero ||
                    _setupCellSlot == IntPtr.Zero || _transitionSlot == IntPtr.Zero)
                    throw new InvalidOperationException("hook slot missing");

                try
                {
                    _config = UnlockLogic.LoadConfig(
                        Path.Combine(MelonUtils.GameDirectory, "OtogiCgUnlock.cfg"));
                    var webAssembly = assemblyOpen(domain, "UnityEngine.UnityWebRequestModule");
                    if (webAssembly == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "UnityWebRequestModule assembly was not found");
                    var webImage = assemblyGetImage(webAssembly);
                    var webRequestClass = classFromName(
                        webImage, "UnityEngine.Networking", "UnityWebRequest");
                    var downloadHandlerClass = classFromName(
                        webImage, "UnityEngine.Networking", "DownloadHandlerBuffer");
                    if (webRequestClass == IntPtr.Zero || downloadHandlerClass == IntPtr.Zero)
                        throw new InvalidOperationException(
                            "UnityWebRequest or DownloadHandlerBuffer was not found");
                    _webRequestGet = FindMethod(webRequestClass, "Get", 1);
                    _webRequestSend = FindMethod(webRequestClass, "SendWebRequest", 0);
                    _webRequestIsDone = FindMethod(webRequestClass, "get_isDone", 0);
                    _webRequestResponseCode = FindMethod(webRequestClass, "get_responseCode", 0);
                    _webRequestDownloadHandler = FindMethod(
                        webRequestClass, "get_downloadHandler", 0);
                    _webRequestAbort = FindMethod(webRequestClass, "Abort", 0);
                    _webRequestDispose = FindMethod(webRequestClass, "Dispose", 0);
                    _downloadHandlerText = FindMethod(downloadHandlerClass, "get_text", 0);
                    QueueRemote("characters.json");
                }
                catch (Exception exception)
                {
                    MelonLogger.Error("[OtogiCgUnlock] transport-failed: " + exception.Message);
                }

                MelonLogger.Msg("[OtogiCgUnlock] installed");
            }
            catch (Exception exception)
            {
                MelonLogger.Error("[OtogiCgUnlock] hook-failed: " + exception.Message);
            }
        }

        internal static void Tick()
        {
            TickDownloadQueue();
        }

        internal static bool TryRewrite(
            string url, string original, int originalStatus, out string json)
        {
            json = original;
            try
            {
                string merged;
                if (TryMergeCharacterList(url, original, out merged))
                {
                    json = merged;
                    return true;
                }

                HandleSceneBody(url, original, originalStatus);
                string substitute;
                if (!TryLoadSubstitute(url, out substitute))
                    return false;
                if (originalStatus != 400 && originalStatus != 404)
                    return false;
                if (Interlocked.Increment(ref _subLogged) <= 64)
                {
                    MelonLogger.Msg(string.Format(
                        CultureInfo.InvariantCulture,
                        "[OtogiCgUnlock] sub url={0} originalStatus={1} chars={2}",
                        new Uri(url).AbsolutePath, originalStatus, substitute.Length));
                }
                PrefetchEpisodeBody(url, substitute);
                json = substitute;
                return true;
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _hookErrorLogged, 1) == 0)
                    MelonLogger.Error("[OtogiCgUnlock] response-error: " + exception.Message);
                return false;
            }
        }

        private static void Attach<T>(
            IntPtr method,
            T detour,
            out T original,
            out IntPtr slot) where T : class
        {
            var target = method == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(method);
            if (target == IntPtr.Zero)
                throw new InvalidOperationException("native pointer missing");
            slot = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(slot, target);
            MelonUtils.NativeHookAttach(
                slot, Marshal.GetFunctionPointerForDelegate((Delegate)(object)detour));
            original = (T)(object)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(slot), detour.GetType());
        }

        private static IntPtr FindMethod(IntPtr klass, string name, int argumentCount)
        {
            while (klass != IntPtr.Zero)
            {
                var method = _classGetMethod(klass, name, argumentCount);
                if (method != IntPtr.Zero)
                    return method;
                klass = _classGetParent(klass);
            }
            throw new MissingMethodException(name);
        }

        private static int GetStatusCode(IntPtr instance, IntPtr methodInfo)
        {
            var code = _originalStatusCode(instance, methodInfo);
            if ((code == 400 || code == 404) && HasSubstitute(instance))
            {
                Marshal.WriteInt32(instance, 0x18, 200);
                return 200;
            }
            return code;
        }

        private static void SetStatusCode(IntPtr instance, int value, IntPtr methodInfo)
        {
            if ((value == 400 || value == 404) && HasSubstitute(instance))
                value = 200;
            _originalSetStatusCode(instance, value, methodInfo);
        }

        private static bool GetIsSuccess(IntPtr instance, IntPtr methodInfo)
        {
            if (HasSubstitute(instance))
            {
                Marshal.WriteInt32(instance, 0x18, 200);
                return true;
            }
            return _originalIsSuccess(instance, methodInfo);
        }

        private static void BypassEpisodeGate(
            IntPtr instance, IntPtr episodeViewModel, IntPtr methodInfo)
        {
            if (episodeViewModel != IntPtr.Zero)
                Marshal.WriteByte(episodeViewModel, 0x35, 1);
            _originalSetupCell(instance, episodeViewModel, methodInfo);
        }

        private static IntPtr MaybeForceAdult(
            IntPtr instance, int isAdult, IntPtr episodeViewModel, IntPtr methodInfo)
        {
            if (isAdult == 0 && episodeViewModel != IntPtr.Zero &&
                Marshal.ReadByte(episodeViewModel, 0x2C) != 0)
            {
                var adultId = Marshal.ReadInt32(episodeViewModel, 0x30);
                if (adultId != 0 &&
                    File.Exists(ScenePath("adults", adultId.ToString(CultureInfo.InvariantCulture))) &&
                    !IsOrdinaryScene(episodeViewModel))
                    isAdult = 1;
            }
            return _originalTransition(instance, isAdult, episodeViewModel, methodInfo);
        }

        private static bool IsOrdinaryScene(IntPtr episodeViewModel)
        {
            if (Marshal.ReadByte(episodeViewModel, 0x20) == 0)
                return false;
            var sceneId = Marshal.ReadInt32(episodeViewModel, 0x24);
            if (sceneId == 0)
                return false;
            var id = sceneId.ToString(CultureInfo.InvariantCulture);
            return File.Exists(ScenePath("scenes", id)) &&
                !File.Exists(ScenePath("adults", id));
        }

        private static bool HasSubstitute(IntPtr response)
        {
            try
            {
                string unused;
                return TryLoadSubstitute(GetResponseUrl(response), out unused);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryLoadSubstitute(string url, out string json)
        {
            json = null;
            string folder;
            string id;
            if (!UnlockLogic.TryParseSceneUrl(url, out folder, out id))
                return false;
            var path = ScenePath(folder, id);
            if (!File.Exists(path))
                return false;
            json = File.ReadAllText(path);
            return !string.IsNullOrEmpty(json);
        }

        private static void HandleSceneBody(string url, string body, int originalStatus)
        {
            string folder;
            string id;
            if (!UnlockLogic.TryParseSceneUrl(url, out folder, out id))
                return;
            var relative = UnlockLogic.CacheRelative(folder, id);
            var action = UnlockLogic.DecideSceneResponse(
                url, originalStatus, File.Exists(CachePath(relative)), RemoteEnabled());
            if (action == SceneResponseAction.Queue)
                QueueRemote(relative);
            if (originalStatus == 200)
                PrefetchEpisodeBody(url, body);
        }

        private static void PrefetchEpisodeBody(string url, string json)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(json))
                return;
            if (url.IndexOf("/api/episode/monsters/", StringComparison.OrdinalIgnoreCase) < 0 &&
                url.IndexOf("/api/episode/spirits/", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            try
            {
                var paths = UnlockLogic.ExtractPrefetchPaths(json);
                for (var i = 0; i < paths.Length; i++)
                    QueueRemote(paths[i]);
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _prefetchErrorLogged, 1) == 0)
                    MelonLogger.Warning("[OtogiCgUnlock] prefetch-error: " + exception.Message);
            }
        }

        private static string ScenePath(string folder, string id)
        {
            return CachePath(UnlockLogic.CacheRelative(folder, id));
        }

        private static string CachePath(string relative)
        {
            var path = Path.Combine(
                MelonUtils.GetApplicationPath(), "UserData", "OtogiCgUnlock");
            if (string.IsNullOrEmpty(relative))
                return path;
            var parts = relative.Split('/');
            for (var i = 0; i < parts.Length; i++)
                path = Path.Combine(path, parts[i]);
            return path;
        }

        private static bool RemoteEnabled()
        {
            return _config != null && _config.RemoteEnabled;
        }

        private static void QueueRemote(string relative)
        {
            if (!RemoteEnabled() || string.IsNullOrEmpty(relative))
                return;
            if (File.Exists(CachePath(relative)))
                return;
            lock (QueueLock)
            {
                if (Pending.Contains(relative) || Unavailable.Contains(relative))
                    return;
                Pending.Add(relative);
                Downloads.Enqueue(relative);
            }
            MelonLogger.Msg("[OtogiCgUnlock] queued relative=" + relative);
        }

        private static void TickDownloadQueue()
        {
            if (_webRequestGet == IntPtr.Zero || !RemoteEnabled())
                return;

            if (_activeRequestHandle != 0)
            {
                PollRequest();
                if (_activeRequestHandle != 0)
                    return;
            }

            if (Stopwatch.GetTimestamp() < _nextRequest)
                return;

            string relative;
            lock (QueueLock)
            {
                if (Downloads.Count == 0)
                    return;
                relative = Downloads.Dequeue();
            }
            StartRequest(relative);
        }

        private static void StartRequest(string relative)
        {
            _activeRelative = relative;
            lock (QueueLock)
            {
                int attempt;
                Attempts.TryGetValue(relative, out attempt);
                _activeAttempt = attempt + 1;
                Attempts[relative] = _activeAttempt;
            }
            try
            {
                var url = UnlockLogic.JoinRemoteUrl(_config.Root, relative);
                var request = Invoke(_webRequestGet, IntPtr.Zero, ToIl2CppString(url));
                _activeRequestHandle = _gcHandleNew(request, false);
                if (_activeRequestHandle == 0)
                    throw new InvalidOperationException("Could not retain UnityWebRequest");
                _activeRequestStarted = Stopwatch.GetTimestamp();
                Invoke(_webRequestSend, request);
                MelonLogger.Msg("[OtogiCgUnlock] request-started relative=" + relative);
            }
            catch (Exception exception)
            {
                FailRequest("request-start-" + exception.GetType().Name);
            }
        }

        private static void PollRequest()
        {
            var request = _gcHandleGetTarget(_activeRequestHandle);
            if (request == IntPtr.Zero)
            {
                FailRequest("request-lost");
                return;
            }
            if (Stopwatch.GetTimestamp() - _activeRequestStarted > 30L * Stopwatch.Frequency)
            {
                FailRequest("timeout");
                return;
            }

            try
            {
                if (!ReadBoxedBoolean(Invoke(_webRequestIsDone, request)))
                    return;
                var status = ReadBoxedInt64(Invoke(_webRequestResponseCode, request));
                if (status < 200 || status >= 300)
                {
                    FailRequest(status == 0
                        ? "unitywebrequest-error"
                        : "http-" + status.ToString(CultureInfo.InvariantCulture));
                    return;
                }

                var download = Invoke(_webRequestDownloadHandler, request);
                var json = ToManagedString(Invoke(_downloadHandlerText, download));
                if (json == null || json.Length > 4 * 1024 * 1024)
                    throw new InvalidDataException("scene JSON is too large");
                var relative = _activeRelative;
                var path = CachePath(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporaryPath = path + ".tmp";
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temporaryPath, path);
                ReleaseRequest(false);
                lock (QueueLock)
                {
                    Pending.Remove(relative);
                    Attempts.Remove(relative);
                }
                MelonLogger.Msg(string.Format(
                    CultureInfo.InvariantCulture,
                    "[OtogiCgUnlock] downloaded relative={0} chars={1}",
                    relative,
                    json.Length));
                if (relative.StartsWith("episodes/", StringComparison.OrdinalIgnoreCase))
                    PrefetchDownloadedEpisode(json);
            }
            catch (Exception exception)
            {
                FailRequest("response-" + exception.GetType().Name);
            }
        }

        private static void PrefetchDownloadedEpisode(string json)
        {
            try
            {
                var paths = UnlockLogic.ExtractPrefetchPaths(json);
                for (var i = 0; i < paths.Length; i++)
                    QueueRemote(paths[i]);
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _prefetchErrorLogged, 1) == 0)
                    MelonLogger.Warning("[OtogiCgUnlock] prefetch-error: " + exception.Message);
            }
        }

        private static void FailRequest(string reason)
        {
            var relative = _activeRelative;
            var attempt = _activeAttempt;
            ReleaseRequest(true);
            if (relative == null)
                return;
            if (IsRetryableRequestFailure(reason) && attempt < 3)
            {
                lock (QueueLock)
                    Downloads.Enqueue(relative);
                _nextRequest = Stopwatch.GetTimestamp() +
                    (1L << (attempt - 1)) * Stopwatch.Frequency;
                MelonLogger.Warning(string.Format(
                    CultureInfo.InvariantCulture,
                    "[OtogiCgUnlock] request-retry relative={0} attempt={1}",
                    relative,
                    attempt + 1));
                return;
            }
            lock (QueueLock)
            {
                Pending.Remove(relative);
                Attempts.Remove(relative);
                if (string.Equals(reason, "http-404", StringComparison.Ordinal))
                    Unavailable.Add(relative);
            }
            MelonLogger.Warning(string.Format(
                CultureInfo.InvariantCulture,
                "[OtogiCgUnlock] download-failed relative={0} reason={1}",
                relative,
                reason));
        }

        private static bool IsRetryableRequestFailure(string reason)
        {
            int status;
            if (!reason.StartsWith("http-", StringComparison.Ordinal) ||
                !int.TryParse(reason.Substring(5), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out status))
                return true;
            return status == 408 || status == 429 || status >= 500;
        }

        private static void ReleaseRequest(bool abort)
        {
            var handle = _activeRequestHandle;
            _activeRequestHandle = 0;
            _activeRequestStarted = 0;
            _activeRelative = null;
            _activeAttempt = 0;
            if (handle == 0)
                return;
            try
            {
                var request = _gcHandleGetTarget(handle);
                if (request != IntPtr.Zero)
                {
                    if (abort)
                        Invoke(_webRequestAbort, request);
                    Invoke(_webRequestDispose, request);
                }
            }
            catch
            {
            }
            finally
            {
                _gcHandleFree(handle);
            }
        }

        private static bool ReadBoxedBoolean(IntPtr value)
        {
            var data = value == IntPtr.Zero ? IntPtr.Zero : _objectUnbox(value);
            if (data == IntPtr.Zero)
                throw new InvalidOperationException("Expected a boxed Boolean");
            return Marshal.ReadByte(data) != 0;
        }

        private static long ReadBoxedInt64(IntPtr value)
        {
            var data = value == IntPtr.Zero ? IntPtr.Zero : _objectUnbox(value);
            if (data == IntPtr.Zero)
                throw new InvalidOperationException("Expected a boxed Int64");
            return Marshal.ReadInt64(data);
        }

        private static bool TryMergeCharacterList(string url, string original, out string json)
        {
            json = null;
            Uri uri;
            if (string.IsNullOrEmpty(original) ||
                string.IsNullOrEmpty(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out uri))
                return false;
            if (uri.AbsolutePath.IndexOf(
                    "/api/Episode/CharacterStory",
                    StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            var extraPath = CachePath("characters.json");
            if (!File.Exists(extraPath))
                return false;
            var body = JObject.Parse(original);
            var extra = JObject.Parse(File.ReadAllText(extraPath));
            var added = MergeById(body, extra, "Monsters", "RootMonsterId") +
                MergeById(body, extra, "Spirits", "RootSpiritId");
            if (added == 0)
                return false;
            json = body.ToString(Newtonsoft.Json.Formatting.None);
            var monsters = body["Monsters"] as JArray;
            var spirits = body["Spirits"] as JArray;
            MelonLogger.Msg(string.Format(
                CultureInfo.InvariantCulture,
                "[OtogiCgUnlock] characters-merged added={0} monsters={1} spirits={2}",
                added,
                monsters == null ? 0 : monsters.Count,
                spirits == null ? 0 : spirits.Count));
            return true;
        }

        private static int MergeById(
            JObject body, JObject extra, string arrayName, string idName)
        {
            var live = body[arrayName] as JArray;
            var more = extra[arrayName] as JArray;
            if (live == null || more == null)
                return 0;
            var have = new HashSet<int>();
            foreach (var row in live)
            {
                var token = row[idName];
                if (token != null && token.Type == JTokenType.Integer)
                    have.Add(token.Value<int>());
            }
            var added = 0;
            foreach (var row in more)
            {
                var token = row[idName];
                if (token == null || token.Type != JTokenType.Integer)
                    continue;
                var id = token.Value<int>();
                if (id == 0 || have.Contains(id))
                    continue;
                live.Add(row);
                have.Add(id);
                added++;
            }
            return added;
        }

        private static string GetResponseUrl(IntPtr response)
        {
            var request = ReadReferenceField(response, "baseRequest");
            if (request == IntPtr.Zero)
                throw new InvalidOperationException("HTTPResponse.baseRequest was null");
            var uri = Invoke(FindMethod(_objectGetClass(request), "get_Uri", 0), request);
            if (uri == IntPtr.Zero)
                throw new InvalidOperationException("HTTPRequest.Uri was null");
            return ToManagedString(
                Invoke(FindMethod(_objectGetClass(uri), "get_AbsoluteUri", 0), uri));
        }

        private static IntPtr ReadReferenceField(IntPtr instance, string name)
        {
            var klass = _objectGetClass(instance);
            IntPtr field = IntPtr.Zero;
            while (klass != IntPtr.Zero && field == IntPtr.Zero)
            {
                field = _classGetField(klass, name);
                klass = _classGetParent(klass);
            }
            if (field == IntPtr.Zero)
                throw new MissingFieldException(name);
            var output = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(output, IntPtr.Zero);
                _fieldGetValue(instance, field, output);
                return Marshal.ReadIntPtr(output);
            }
            finally
            {
                Marshal.FreeHGlobal(output);
            }
        }

        private static IntPtr Invoke(IntPtr method, IntPtr instance, params IntPtr[] arguments)
        {
            var parameters = IntPtr.Zero;
            try
            {
                if (arguments != null && arguments.Length != 0)
                {
                    parameters = Marshal.AllocHGlobal(IntPtr.Size * arguments.Length);
                    for (var index = 0; index < arguments.Length; index++)
                        Marshal.WriteIntPtr(parameters, index * IntPtr.Size, arguments[index]);
                }
                var exception = IntPtr.Zero;
                var result = _runtimeInvoke(method, instance, parameters, ref exception);
                if (exception != IntPtr.Zero)
                    throw new InvalidOperationException("IL2CPP invocation failed");
                return result;
            }
            finally
            {
                if (parameters != IntPtr.Zero)
                    Marshal.FreeHGlobal(parameters);
            }
        }

        private static string ToManagedString(IntPtr value)
        {
            if (value == IntPtr.Zero)
                return null;
            return Marshal.PtrToStringUni(_stringChars(value), _stringLength(value));
        }

        private static IntPtr ToIl2CppString(string value)
        {
            var chars = Marshal.StringToHGlobalUni(value);
            try
            {
                return _stringNewUtf16(chars, value.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(chars);
            }
        }
    }
#endif
}
