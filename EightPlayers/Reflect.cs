using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EightPlayers
{
    // A general-purpose "God-view" reflection surface over the LIVE game object
    // graph. Lets the MCP inspect and mutate ANY object the running game exposes
    // — no per-feature C# verb needed. Everything is best-effort and defensive:
    // every member read/write/call is wrapped so a bad getter records "<err>"
    // instead of throwing, and nothing here can hang or crash the game.
    //
    // Targets (a small prefix scheme, shared by inspect/get/set/call):
    //   gc                 the GameController singleton (GameController.gameController)
    //   agent:<uid>        a live agent by UID (GameStateApi.FindAgent)
    //   handle:<id>        an object previously returned by find/get/call
    //   static:<TypeName>  the Type itself, for static members
    //
    // Handles: reference-typed results (from find/get/call) are stashed in a
    // WeakReference registry and addressable later as handle:<id>.
    public static class Reflect
    {
        private const int MaxOutput = 8192;     // ~8KB cap on any single reply
        private const int MaxCollection = 12;   // first-N items summarized for collections
        private const BindingFlags AllMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.FlattenHierarchy;

        // ---- handle registry --------------------------------------------------
        private static readonly Dictionary<int, WeakReference> Handles = new Dictionary<int, WeakReference>();
        private static int _nextHandle = 1;

        public static int Register(object o)
        {
            int id = _nextHandle++;
            Handles[id] = new WeakReference(o);
            return id;
        }

        private static object ResolveHandle(int id)
        {
            return Handles.TryGetValue(id, out var w) && w.IsAlive ? w.Target : null;
        }

        // ---- target resolution -----------------------------------------------
        private struct Target
        {
            public object Obj;   // null for static targets
            public Type Type;
        }

        private static Target ResolveTarget(string spec)
        {
            if (string.IsNullOrEmpty(spec))
                throw new ArgumentException("empty target");
            if (spec == "gc")
            {
                var gc = GameController.gameController;
                if (gc == null) throw new InvalidOperationException("no GameController");
                return new Target { Obj = gc, Type = gc.GetType() };
            }
            if (spec.StartsWith("agent:", StringComparison.OrdinalIgnoreCase))
            {
                var a = GameStateApi.FindAgent(int.Parse(spec.Substring(6)));
                if (a == null) throw new ArgumentException($"no agent {spec.Substring(6)}");
                return new Target { Obj = a, Type = a.GetType() };
            }
            if (spec.StartsWith("handle:", StringComparison.OrdinalIgnoreCase))
            {
                var o = ResolveHandle(int.Parse(spec.Substring(7)));
                if (o == null) throw new ArgumentException($"handle {spec.Substring(7)} is dead or unknown");
                return new Target { Obj = o, Type = o.GetType() };
            }
            if (spec.StartsWith("static:", StringComparison.OrdinalIgnoreCase))
            {
                var t = FindType(spec.Substring(7));
                if (t == null) throw new ArgumentException($"no type '{spec.Substring(7)}'");
                return new Target { Obj = null, Type = t };
            }
            throw new ArgumentException($"unknown target '{spec}' (use gc | agent:<uid> | handle:<id> | static:<Type>)");
        }

        public static Type FindType(string name)
        {
            var direct = Type.GetType(name, throwOnError: false);
            if (direct != null) return direct;
            Type byFull = null, bySimple = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t.FullName == name) return t;
                    if (byFull == null && string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase)) byFull = t;
                    if (bySimple == null && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) bySimple = t;
                }
            }
            return byFull ?? bySimple;
        }

        // ---- public verbs -----------------------------------------------------

        /// <summary>Dump every field + property of a target as JSON, recursing one
        /// level into nested reference objects and summarizing collections.</summary>
        public static string Inspect(string targetSpec)
        {
            try
            {
                var t = ResolveTarget(targetSpec);
                var root = new JObject
                {
                    ["$target"] = targetSpec,
                    ["$type"] = t.Type.FullName,
                };
                if (t.Obj != null) root["$toString"] = SafeToString(t.Obj);
                root["members"] = DumpMembers(t.Obj, t.Type, depth: 1);
                return Cap(root);
            }
            catch (Exception e) { return Err(e); }
        }

        /// <summary>Read a dotted path (fields/props, [i] indexers, [key] dict keys)
        /// off a target root; scalars come back JSON-encoded, reference types as a
        /// handle string.</summary>
        public static string Get(string targetSpec, string path)
        {
            try
            {
                var t = ResolveTarget(targetSpec);
                var (value, _) = Navigate(t.Obj, t.Type, path);
                return Cap(EncodeResult(value));
            }
            catch (Exception e) { return Err(e); }
        }

        /// <summary>Write a field/property at a dotted path, coercing the raw string
        /// to the member's type. Returns the new value.</summary>
        public static string Set(string targetSpec, string path, string rawValue)
        {
            try
            {
                var t = ResolveTarget(targetSpec);
                var segs = ParsePath(path);
                if (segs.Count == 0) throw new ArgumentException("empty path");
                // Walk to the parent of the final segment.
                object obj = t.Obj;
                Type type = t.Type;
                for (int i = 0; i < segs.Count - 1; i++)
                {
                    var (v, vt) = StepInto(obj, type, segs[i]);
                    obj = v; type = vt;
                }
                var last = segs[segs.Count - 1];
                object written = WriteMember(obj, type, last, rawValue);
                return Cap(EncodeResult(written));
            }
            catch (Exception e) { return Err(e); }
        }

        /// <summary>Invoke a method by name with a JSON-array of args coerced to the
        /// parameter types. Returns the result (JSON scalar or handle).</summary>
        public static string Call(string targetSpec, string method, string jsonArgs)
        {
            try
            {
                var t = ResolveTarget(targetSpec);
                JArray args = string.IsNullOrWhiteSpace(jsonArgs) ? new JArray() : JArray.Parse(jsonArgs);
                var candidates = t.Type.GetMethods(AllMembers)
                    .Where(m => m.Name == method && m.GetParameters().Length == args.Count)
                    .ToList();
                if (candidates.Count == 0)
                    throw new ArgumentException($"no method '{method}' with {args.Count} arg(s) on {t.Type.Name}");
                Exception last = null;
                foreach (var m in candidates)
                {
                    try
                    {
                        var ps = m.GetParameters();
                        var coerced = new object[ps.Length];
                        for (int i = 0; i < ps.Length; i++)
                            coerced[i] = CoerceToken(args[i], ps[i].ParameterType);
                        object result = m.Invoke(m.IsStatic ? null : t.Obj, coerced);
                        if (m.ReturnType == typeof(void)) return "void (ok)";
                        return Cap(EncodeResult(result));
                    }
                    catch (Exception e) { last = e; }
                }
                throw last ?? new ArgumentException("no overload matched");
            }
            catch (Exception e) { return Err(e); }
        }

        /// <summary>FindObjectsOfType(TypeName) → register each and return handles.</summary>
        public static string Find(string typeName, int max)
        {
            try
            {
                var type = FindType(typeName);
                if (type == null) throw new ArgumentException($"no type '{typeName}'");
                if (!typeof(UnityEngine.Object).IsAssignableFrom(type))
                    throw new ArgumentException($"'{type.Name}' is not a UnityEngine.Object (find only works on those)");
                var found = UnityEngine.Object.FindObjectsOfType(type);
                var arr = new JArray();
                for (int i = 0; i < found.Length && i < max; i++)
                {
                    arr.Add(new JObject
                    {
                        ["handle"] = Register(found[i]),
                        ["type"] = found[i].GetType().Name,
                        ["toString"] = SafeToString(found[i]),
                    });
                }
                return Cap(new JObject { ["type"] = type.FullName, ["total"] = found.Length, ["results"] = arr });
            }
            catch (Exception e) { return Err(e); }
        }

        /// <summary>List fields/properties/methods of a type for discovery.</summary>
        public static string Members(string typeName)
        {
            try
            {
                var type = FindType(typeName);
                if (type == null) throw new ArgumentException($"no type '{typeName}'");
                var fields = new JArray();
                foreach (var f in type.GetFields(AllMembers))
                    fields.Add($"{(f.IsStatic ? "static " : "")}{TypeName(f.FieldType)} {f.Name}");
                var props = new JArray();
                foreach (var p in type.GetProperties(AllMembers))
                    props.Add($"{TypeName(p.PropertyType)} {p.Name}{(p.CanRead ? " get" : "")}{(p.CanWrite ? " set" : "")}");
                var methods = new JArray();
                foreach (var m in type.GetMethods(AllMembers))
                {
                    if (m.IsSpecialName) continue; // skip property accessors
                    var ps = string.Join(", ", m.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name}"));
                    methods.Add($"{(m.IsStatic ? "static " : "")}{TypeName(m.ReturnType)} {m.Name}({ps})");
                }
                return Cap(new JObject
                {
                    ["type"] = type.FullName,
                    ["fields"] = fields,
                    ["properties"] = props,
                    ["methods"] = methods,
                });
            }
            catch (Exception e) { return Err(e); }
        }

        /// <summary>Enumerate loaded types whose name contains a substring.</summary>
        public static string Types(string substring, int max)
        {
            try
            {
                var hits = new List<string>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    catch { continue; }
                    foreach (var t in types)
                    {
                        if (t.FullName != null && t.FullName.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hits.Add(t.FullName);
                            if (hits.Count >= max) goto done;
                        }
                    }
                }
                done:
                return Cap(new JObject { ["match"] = substring, ["count"] = hits.Count, ["types"] = new JArray(hits) });
            }
            catch (Exception e) { return Err(e); }
        }

        // ---- path navigation --------------------------------------------------
        private struct Seg { public string Name; public bool HasIndex; public string Index; }

        private static List<Seg> ParsePath(string path)
        {
            var segs = new List<Seg>();
            if (string.IsNullOrWhiteSpace(path)) return segs;
            foreach (var raw in path.Split('.'))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                int b = part.IndexOf('[');
                if (b >= 0 && part.EndsWith("]"))
                {
                    segs.Add(new Seg
                    {
                        Name = part.Substring(0, b),
                        HasIndex = true,
                        Index = part.Substring(b + 1, part.Length - b - 2),
                    });
                }
                else segs.Add(new Seg { Name = part, HasIndex = false });
            }
            return segs;
        }

        private static (object, Type) Navigate(object obj, Type type, string path)
        {
            foreach (var seg in ParsePath(path))
            {
                var (v, vt) = StepInto(obj, type, seg);
                obj = v; type = vt;
            }
            return (obj, type);
        }

        private static (object, Type) StepInto(object obj, Type type, Seg seg)
        {
            object value = string.IsNullOrEmpty(seg.Name) ? obj : ReadMember(obj, type, seg.Name);
            if (seg.HasIndex)
                value = IndexInto(value, seg.Index);
            var vt = value?.GetType() ?? typeof(object);
            return (value, vt);
        }

        private static object ReadMember(object obj, Type type, string name)
        {
            var f = type.GetField(name, AllMembers);
            if (f != null) return f.GetValue(f.IsStatic ? null : obj);
            var p = type.GetProperty(name, AllMembers);
            if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                return p.GetValue((p.GetMethod?.IsStatic ?? false) ? null : obj);
            throw new ArgumentException($"no readable member '{name}' on {type.Name}");
        }

        private static object WriteMember(object obj, Type type, Seg seg, string rawValue)
        {
            if (seg.HasIndex)
            {
                // set collection element: read the member, then write into it
                object coll = string.IsNullOrEmpty(seg.Name) ? obj : ReadMember(obj, type, seg.Name);
                return WriteIndex(coll, seg.Index, rawValue);
            }
            var f = type.GetField(seg.Name, AllMembers);
            if (f != null)
            {
                object cv = CoerceString(rawValue, f.FieldType);
                f.SetValue(f.IsStatic ? null : obj, cv);
                return cv;
            }
            var p = type.GetProperty(seg.Name, AllMembers);
            if (p != null && p.CanWrite)
            {
                object cv = CoerceString(rawValue, p.PropertyType);
                p.SetValue((p.SetMethod?.IsStatic ?? false) ? null : obj, cv);
                return cv;
            }
            throw new ArgumentException($"no writable member '{seg.Name}' on {type.Name}");
        }

        private static object IndexInto(object coll, string key)
        {
            if (coll == null) throw new ArgumentException("cannot index null");
            if (coll is Array a) return a.GetValue(int.Parse(key));
            if (coll is IDictionary dict)
            {
                if (dict.Contains(key)) return dict[key];
                if (int.TryParse(key, out var ik) && dict.Contains(ik)) return dict[ik];
                throw new ArgumentException($"key '{key}' not in dictionary");
            }
            if (coll is IList list) return list[int.Parse(key)];
            var indexer = coll.GetType().GetProperty("Item", AllMembers);
            if (indexer != null)
                return indexer.GetValue(coll, new object[] { CoerceString(key, indexer.GetIndexParameters()[0].ParameterType) });
            throw new ArgumentException($"{coll.GetType().Name} is not indexable");
        }

        private static object WriteIndex(object coll, string key, string rawValue)
        {
            if (coll == null) throw new ArgumentException("cannot index null");
            if (coll is Array a)
            {
                object cv = CoerceString(rawValue, a.GetType().GetElementType());
                a.SetValue(cv, int.Parse(key));
                return cv;
            }
            if (coll is IDictionary dict)
            {
                object cv = CoerceString(rawValue, typeof(object));
                dict[key] = cv;
                return cv;
            }
            if (coll is IList list)
            {
                object cv = CoerceString(rawValue, typeof(object));
                list[int.Parse(key)] = cv;
                return cv;
            }
            throw new ArgumentException($"{coll.GetType().Name} is not index-writable");
        }

        // ---- value encoding ---------------------------------------------------

        private static JObject DumpMembers(object obj, Type type, int depth)
        {
            var result = new JObject();
            foreach (var f in type.GetFields(AllMembers))
            {
                try { result[f.Name] = Summarize(f.GetValue(f.IsStatic ? null : obj), depth); }
                catch (Exception e) { result[f.Name] = "<err> " + Root(e).Message; }
            }
            foreach (var p in type.GetProperties(AllMembers))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (result.ContainsKey(p.Name)) continue;
                try { result[p.Name] = Summarize(p.GetValue((p.GetMethod?.IsStatic ?? false) ? null : obj), depth); }
                catch (Exception e) { result[p.Name] = "<err> " + Root(e).Message; }
            }
            return result;
        }

        // Recurse one level for reference objects; summarize collections with a
        // count + first-N; scalars/vectors inline; deeper refs become handles.
        private static JToken Summarize(object v, int depth)
        {
            if (v == null || (v is UnityEngine.Object uo && uo == null)) return JValue.CreateNull();
            var t = v.GetType();
            if (IsScalar(t)) return ScalarToken(v);
            var vec = VectorToken(v);
            if (vec != null) return vec;

            if (v is IEnumerable en && !(v is string))
            {
                var items = new JArray();
                int n = 0, shown = 0;
                foreach (var it in en)
                {
                    n++;
                    if (shown < MaxCollection)
                    {
                        try { items.Add(Summarize(it, depth - 1)); shown++; }
                        catch (Exception e) { items.Add("<err> " + Root(e).Message); shown++; }
                    }
                }
                return new JObject { ["$type"] = t.Name, ["count"] = n, ["items"] = items };
            }

            if (depth <= 0)
                return HandleToken(v);

            // recurse one level into the reference object's members
            var o = new JObject { ["$type"] = t.Name, ["$handle"] = Register(v) };
            if (v is UnityEngine.Object) o["$name"] = SafeToString(v);
            o["members"] = DumpMembers(v, t, depth - 1);
            return o;
        }

        // For get/call results: scalar → JSON; reference → handle string.
        private static JToken EncodeResult(object v)
        {
            if (v == null || (v is UnityEngine.Object uo && uo == null)) return JValue.CreateNull();
            var t = v.GetType();
            if (IsScalar(t)) return ScalarToken(v);
            var vec = VectorToken(v);
            if (vec != null) return vec;
            return HandleToken(v);
        }

        private static JToken HandleToken(object v)
        {
            return new JObject
            {
                ["handle"] = Register(v),
                ["type"] = v.GetType().Name,
                ["toString"] = SafeToString(v),
            };
        }

        private static bool IsScalar(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);
        }

        private static JToken ScalarToken(object v)
        {
            if (v is Enum) return new JValue(v.ToString());
            return new JValue(v);
        }

        private static JToken VectorToken(object v)
        {
            switch (v)
            {
                case Vector2 v2: return new JObject { ["x"] = v2.x, ["y"] = v2.y };
                case Vector3 v3: return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
                case Vector2Int vi: return new JObject { ["x"] = vi.x, ["y"] = vi.y };
                case Vector3Int vi3: return new JObject { ["x"] = vi3.x, ["y"] = vi3.y, ["z"] = vi3.z };
                case Quaternion q: return new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };
                case Color c: return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
                default: return null;
            }
        }

        // ---- coercion ---------------------------------------------------------

        private static object CoerceString(string raw, Type t)
        {
            if (t == typeof(string)) return raw;
            if (t == typeof(object)) // best-effort infer
            {
                if (bool.TryParse(raw, out var b)) return b;
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
                if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
                return raw;
            }
            if (t == typeof(bool)) return bool.Parse(raw);
            if (t.IsEnum) return Enum.Parse(t, raw, ignoreCase: true);
            if (t == typeof(Vector2)) { var p = SplitFloats(raw, 2); return new Vector2(p[0], p[1]); }
            if (t == typeof(Vector3)) { var p = SplitFloats(raw, 3); return new Vector3(p[0], p[1], p[2]); }
            var nullable = Nullable.GetUnderlyingType(t);
            if (nullable != null) return string.IsNullOrEmpty(raw) ? null : CoerceString(raw, nullable);
            return Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
        }

        private static object CoerceToken(JToken tok, Type t)
        {
            if (t == typeof(string)) return tok.Type == JTokenType.Null ? null : tok.ToString();
            if (t == typeof(Vector2))
            {
                if (tok is JArray a2) return new Vector2((float)a2[0], (float)a2[1]);
                var p = SplitFloats(tok.ToString(), 2); return new Vector2(p[0], p[1]);
            }
            if (t == typeof(Vector3))
            {
                if (tok is JArray a3) return new Vector3((float)a3[0], (float)a3[1], (float)a3[2]);
                var p = SplitFloats(tok.ToString(), 3); return new Vector3(p[0], p[1], p[2]);
            }
            if (t.IsEnum)
                return tok.Type == JTokenType.String
                    ? Enum.Parse(t, tok.ToString(), ignoreCase: true)
                    : Enum.ToObject(t, tok.ToObject<long>());
            if (tok.Type == JTokenType.Null) return null;
            // handle:<id> reference args passed as a string
            if (tok.Type == JTokenType.String && tok.ToString().StartsWith("handle:", StringComparison.OrdinalIgnoreCase))
            {
                var o = ResolveHandle(int.Parse(tok.ToString().Substring(7)));
                if (o != null && t.IsInstanceOfType(o)) return o;
            }
            return tok.ToObject(t);
        }

        private static float[] SplitFloats(string raw, int n)
        {
            var parts = raw.Split(',');
            if (parts.Length < n) throw new ArgumentException($"expected {n} comma-separated numbers, got '{raw}'");
            var r = new float[n];
            for (int i = 0; i < n; i++) r[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
            return r;
        }

        // ---- misc helpers -----------------------------------------------------

        private static string TypeName(Type t)
        {
            if (t == null) return "?";
            if (!t.IsGenericType) return t.Name;
            var args = string.Join(",", t.GetGenericArguments().Select(TypeName));
            var baseName = t.Name;
            int tick = baseName.IndexOf('`');
            if (tick >= 0) baseName = baseName.Substring(0, tick);
            return $"{baseName}<{args}>";
        }

        private static string SafeToString(object o)
        {
            try { return o?.ToString() ?? "null"; }
            catch (Exception e) { return "<toString err> " + Root(e).Message; }
        }

        private static Exception Root(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        private static string Err(Exception e)
        {
            e = Root(e);
            return $"error: {e.GetType().Name}: {e.Message}";
        }

        private static string Cap(JToken token)
        {
            return Cap(token.ToString(Formatting.None));
        }

        private static string Cap(string s)
        {
            if (s.Length <= MaxOutput) return s;
            return s.Substring(0, MaxOutput) + $"...(truncated, {s.Length - MaxOutput} more chars)";
        }
    }
}
