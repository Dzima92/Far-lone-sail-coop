using System;
using System.Collections.Generic;
using Okomotive.FarLoneSails;
using UnhollowerBaseLib;
using UnhollowerRuntimeLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Scene = UnityEngine.SceneManagement.Scene;
using SceneManager = UnityEngine.SceneManagement.SceneManager;

namespace FarCoopMod
{
    /// <summary>Safe helpers for finding game objects from the unhollowed game assembly.</summary>
    public static class GameAccess
    {
        private static Character cachedCharacter;
        private static float nextCharacterSearch;

        public static bool Alive(UnityEngine.Object o)
        {
            if (ReferenceEquals(o, null)) return false;
            try
            {
                if (o.WasCollected || o.Pointer == IntPtr.Zero) return false;
                return o != null; // Unity's overloaded null check (destroyed objects)
            }
            catch { return false; }
        }

        public static T GetComp<T>(GameObject go) where T : Component
        {
            if (!Alive(go)) return null;
            var c = go.GetComponent(Il2CppType.Of<T>());
            return c == null ? null : c.TryCast<T>();
        }

        public static T GetCompInChildren<T>(GameObject go) where T : Component
        {
            if (!Alive(go)) return null;
            var c = go.GetComponentInChildren(Il2CppType.Of<T>(), true);
            return c == null ? null : c.TryCast<T>();
        }

        public static List<T> GetCompsInChildren<T>(GameObject go) where T : Component
        {
            var list = new List<T>();
            if (!Alive(go)) return list;
            var arr = go.GetComponentsInChildren(Il2CppType.Of<T>(), true);
            if (arr == null) return list;
            for (int i = 0; i < arr.Length; i++)
            {
                var t = arr[i]?.TryCast<T>();
                if (t != null) list.Add(t);
            }
            return list;
        }

        /// <summary>The local player's character (null in menus).</summary>
        public static Character LocalCharacter
        {
            get
            {
                if (Alive(cachedCharacter) && !PartnerInput.IsPuppet(cachedCharacter.Pointer)) return cachedCharacter;
                cachedCharacter = null;
                if (Time.unscaledTime < nextCharacterSearch) return null;
                nextCharacterSearch = Time.unscaledTime + 1f;
                try
                {
                    var all = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Character>());
                    for (int i = 0; i < all.Length; i++)
                    {
                        var c = all[i]?.TryCast<Character>();
                        if (c == null || PartnerInput.IsPuppet(c.Pointer)) continue;
                        try { if (c.gameObject.name.StartsWith("FarCoop_")) continue; } catch { }
                        cachedCharacter = c;
                        break;
                    }
                }
                catch (Exception e) { FarCoopPlugin.Logger.LogDebug("Character search: " + e.Message); }
                return cachedCharacter;
            }
        }

        public static Animator CharacterAnimator(Character c)
        {
            if (!Alive(c)) return null;
            try { var a = c.animator; if (Alive(a)) return a; } catch { }
            try { return GetCompInChildren<Animator>(c.gameObject); } catch { return null; }
        }

        public static bool CharacterInsideVehicle(Character c)
        {
            try { return c.insideVehicle; } catch { return false; }
        }

        public static Rigidbody CharacterBody(Character c)
        {
            if (!Alive(c)) return null;
            var rb = GetComp<Rigidbody>(c.gameObject);
            if (rb != null) return rb;
            try { return c.GetComponentInParent(Il2CppType.Of<Rigidbody>())?.TryCast<Rigidbody>(); } catch { return null; }
        }

        // ---------- stable hierarchy paths (same scene on both PCs => same path) ----------

        /// <summary>"scene|rootName#occurrence|childIdx|childIdx..."</summary>
        public static string GetPath(Transform t)
        {
            var idx = new List<int>();
            while (t.parent != null)
            {
                idx.Add(t.GetSiblingIndex());
                t = t.parent;
            }
            idx.Reverse();

            GameObject rootGo = t.gameObject;
            Scene scene = rootGo.scene;
            int occurrence = 0;
            var roots = scene.GetRootGameObjects();
            string rootName = rootGo.name;
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].Pointer == rootGo.Pointer) break;
                if (roots[i].name == rootName) occurrence++;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(scene.name).Append('|').Append(rootName).Append('#').Append(occurrence);
            foreach (int i in idx) sb.Append('|').Append(i);
            return sb.ToString();
        }

        public static GameObject FindByPath(string path)
        {
            try
            {
                string[] parts = path.Split('|');
                if (parts.Length < 2) return null;
                Scene scene = SceneManager.GetSceneByName(parts[0]);
                if (!scene.IsValid() || !scene.isLoaded) return null;

                int hash = parts[1].LastIndexOf('#');
                string rootName = parts[1].Substring(0, hash);
                int occurrence = int.Parse(parts[1].Substring(hash + 1));

                var roots = scene.GetRootGameObjects();
                GameObject root = null;
                int seen = 0;
                for (int i = 0; i < roots.Length; i++)
                {
                    if (roots[i].name != rootName) continue;
                    if (seen++ == occurrence) { root = roots[i]; break; }
                }
                if (root == null) return null;

                Transform t = root.transform;
                for (int p = 2; p < parts.Length; p++)
                {
                    int ci = int.Parse(parts[p]);
                    if (ci < 0 || ci >= t.childCount) return null;
                    t = t.GetChild(ci);
                }
                return t.gameObject;
            }
            catch { return null; }
        }

        /// <summary>Path relative to an ancestor: "idx/idx/idx" (empty for the ancestor itself).</summary>
        public static string GetRelativePath(Transform t, Transform ancestor)
        {
            var idx = new List<string>();
            while (t != null && t.Pointer != ancestor.Pointer)
            {
                idx.Add(t.GetSiblingIndex().ToString());
                idx.Add(t.name);
                t = t.parent;
            }
            idx.Reverse();
            return string.Join("/", idx);
        }
    }
}
