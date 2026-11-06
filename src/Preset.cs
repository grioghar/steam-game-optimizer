using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace SteamOptimizer
{
    public class PresetResult
    {
        public bool Ok;
        public string Name = "";
        public int Changes;
        public List<string> Lines = new List<string>();
        public List<string> Touched = new List<string>();   // lower-case root keys present in the file
    }

    /// <summary>
    /// Applies a JSON preset (a partial or full config file) onto the live configuration.
    /// Default = merge. Validation is all-or-nothing: any ERROR rejects the whole file.
    /// </summary>
    public static class Preset
    {
        public static string Dir { get { return Path.Combine(Store.Dir, "presets"); } }
        public static string AppliedDir { get { return Path.Combine(Dir, "applied"); } }
        public static string RejectedDir { get { return Path.Combine(Dir, "rejected"); } }
        public static string ExamplesDir { get { return Path.Combine(Dir, "examples"); } }

        static readonly string[] Meta = { "name", "description", "version", "importmode", "removerules", "$schema" };
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

        /// <summary>Creates the drop folders and unpacks the bundled example presets (never overwrites).</summary>
        public static void EnsureFolders()
        {
            try
            {
                Directory.CreateDirectory(Dir); Directory.CreateDirectory(AppliedDir); Directory.CreateDirectory(RejectedDir); Directory.CreateDirectory(ExamplesDir);
                Assembly a = Assembly.GetExecutingAssembly();
                foreach (string n in a.GetManifestResourceNames())
                {
                    if (!n.StartsWith("ex.")) continue;
                    string dest = Path.Combine(ExamplesDir, n.Substring(3));
                    if (File.Exists(dest)) continue;
                    using (Stream s = a.GetManifestResourceStream(n)) using (FileStream f = File.Create(dest)) s.CopyTo(f);
                }
                string readme = Path.Combine(Dir, "README.txt");
                if (!File.Exists(readme))
                    File.WriteAllText(readme,
                        "Drop a .json preset into this folder and Steam Game Optimizer applies it within a few seconds.\r\n" +
                        "Applied files move to 'applied', rejected files move to 'rejected' (with a .txt explaining why).\r\n" +
                        "Examples to copy from are in the 'examples' folder. Full reference: docs/CONFIGURATION.md in the project repository.\r\n");
            }
            catch { }
        }

        static string NormAction(object v)
        {
            string s = v as string; if (s == null) return null;
            s = s.Trim().ToLowerInvariant();
            if (s == "keep") return Act.Keep;
            if (s == "suspend" || s == "freeze") return Act.Suspend;
            if (s == "close" || s == "quit") return Act.Close;
            return null;
        }
        static string NormName(string n)
        {
            n = n.Trim().ToLowerInvariant();
            if (n.EndsWith(".exe")) n = n.Substring(0, n.Length - 4);
            return n;
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            foreach (KeyValuePair<string, object> kv in d) if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        public static void CopyFields(object src, object dst)
        {
            foreach (FieldInfo f in src.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) f.SetValue(dst, f.GetValue(src));
        }

        public static PresetResult Apply(AppConfig cfg, string json)
        {
            PresetResult r = new PresetResult();
            JavaScriptSerializer ser = new JavaScriptSerializer();
            Dictionary<string, object> d = null;
            try { d = ser.DeserializeObject(json) as Dictionary<string, object>; }
            catch (Exception ex) { r.Lines.Add("ERROR: not valid JSON (" + ex.Message + ")"); return r; }
            if (d == null) { r.Lines.Add("ERROR: the file must contain a JSON object ({ ... })"); return r; }

            foreach (string k in d.Keys) r.Touched.Add(k.TrimEnd('!').ToLowerInvariant());
            object nm = Get(d, "Name"); if (nm is string) r.Name = (string)nm;

            AppConfig work = ser.Deserialize<AppConfig>(ser.Serialize(cfg));
            string mode = "merge"; object mv = Get(d, "ImportMode");
            if (mv != null) { mode = (mv as string ?? "").Trim().ToLowerInvariant(); if (mode != "merge" && mode != "replace") { r.Lines.Add("ERROR: ImportMode must be \"merge\" or \"replace\""); return r; } }
            if (mode == "replace") { CopyFields(AppConfig.CreateDefault(), work); r.Lines.Add("ImportMode=replace: starting from built-in defaults."); }

            ApplyObject(work, d, "", r);

            object rm = Get(d, "RemoveRules");
            if (rm != null)
            {
                if (rm is IEnumerable && !(rm is string))
                    foreach (object o in (IEnumerable)rm) { string n = o as string; if (n != null && work.Rules.Remove(NormName(n))) r.Changes++; }
                else r.Lines.Add("ERROR: RemoveRules must be a list of app names");
            }

            r.Ok = !r.Lines.Exists(delegate(string l) { return l.StartsWith("ERROR"); });
            if (r.Ok) CopyFields(work, cfg);
            return r;
        }

        static void ApplyObject(object target, Dictionary<string, object> d, string path, PresetResult r)
        {
            Type t = target.GetType();
            foreach (KeyValuePair<string, object> kv in d)
            {
                string key = kv.Key;
                if (key.StartsWith("_")) continue;
                if (path.Length == 0 && Array.IndexOf(Meta, key.ToLowerInvariant()) >= 0) continue;
                bool replace = key.EndsWith("!"); if (replace) key = key.Substring(0, key.Length - 1);
                FieldInfo f = t.GetField(key, Flags);
                if (f == null) { r.Lines.Add("warning: unknown setting '" + path + key + "' ignored"); continue; }
                string p = path + f.Name; object v = kv.Value; Type ft = f.FieldType;
                try
                {
                    if (ft == typeof(bool))
                    {
                        if (!(v is bool)) throw new Exception("expected true or false");
                        f.SetValue(target, v);
                    }
                    else if (ft == typeof(int))
                    {
                        if (!(v is int || v is long || v is decimal || v is double)) throw new Exception("expected a whole number");
                        int n = Convert.ToInt32(v); if (n < 0) throw new Exception("must be 0 or more");
                        f.SetValue(target, n);
                    }
                    else if (ft == typeof(string))
                    {
                        if (!(v is string)) throw new Exception("expected text");
                        if (f.Name.StartsWith("Default")) { string a = NormAction(v); if (a == null) throw new Exception("must be Keep, Suspend or Close"); f.SetValue(target, a); }
                        else f.SetValue(target, ((string)v).Trim());
                    }
                    else if (ft == typeof(List<string>))
                    {
                        if (!(v is IEnumerable) || v is string) throw new Exception("expected a list of text values");
                        List<string> list = (List<string>)f.GetValue(target); if (replace) list.Clear();
                        foreach (object o in (IEnumerable)v)
                        {
                            string s = o as string; if (s == null) throw new Exception("list items must be text");
                            s = s.Trim(); if (s.Length == 0) continue;
                            if (!list.Exists(delegate(string x) { return string.Equals(x, s, StringComparison.OrdinalIgnoreCase); })) list.Add(s);
                        }
                    }
                    else if (ft == typeof(Dictionary<string, string>))
                    {
                        Dictionary<string, object> rules = v as Dictionary<string, object>;
                        if (rules == null) throw new Exception("expected an object like { \"appname\": \"Close\" }");
                        Dictionary<string, string> dict = (Dictionary<string, string>)f.GetValue(target); if (replace) dict.Clear();
                        foreach (KeyValuePair<string, object> rv in rules)
                        {
                            if (rv.Key.StartsWith("_")) continue;
                            string name = NormName(rv.Key); if (name.Length == 0) continue;
                            string rs = rv.Value as string;
                            if (rv.Value == null || (rs != null && (rs.Trim().ToLowerInvariant() == "(auto)" || rs.Trim().ToLowerInvariant() == "auto"))) { dict.Remove(name); continue; }
                            string act = NormAction(rv.Value);
                            if (act == null) throw new Exception("rule '" + rv.Key + "' must be Keep, Suspend, Close or \"auto\"");
                            dict[name] = act;
                        }
                    }
                    else if (ft == typeof(ModeProfile))
                    {
                        Dictionary<string, object> sub = v as Dictionary<string, object>;
                        if (sub == null) throw new Exception("expected an object with profile settings");
                        ApplyObject(f.GetValue(target), sub, p + ".", r);
                        continue;
                    }
                    else throw new Exception("unsupported setting type");
                    r.Changes++;
                }
                catch (Exception ex) { r.Lines.Add("ERROR: " + p + ": " + ex.Message); }
            }
        }
    }
}
