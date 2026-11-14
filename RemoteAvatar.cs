using System;
using System.Collections.Generic;
using System.IO;
using FarCoopMod.Net;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace FarCoopMod
{
    /// <summary>
    /// The partner's body: a visual-only clone of the local character model (all game scripts,
    /// colliders and rigidbodies stripped), driven by received transform + animator state.
    /// </summary>
    public sealed class RemoteAvatar
    {
        private const byte FlagInsideVehicle = 1;
        private const byte FlagHasAnim = 2;

        private GameObject root;
        private Transform model;
        private bool isCapsule;
        private Animator anim;
        private AnimatorControllerParameter[] animParams;
        private float nextBuildAttempt;

        private bool hasTarget;
        private bool targetInside;
        private Vector3 targetPos;
        private Quaternion targetRot = Quaternion.identity;
        private Vector3 targetScale = Vector3.one;
        private float[] targetParams;
        private int targetStateHash;
        private float targetStateTime;
        public float LastUpdateTime { get; private set; }

        public bool Exists => GameAccess.Alive(root);
        public Vector3 WorldPosition => Exists ? root.transform.position : Vector3.zero;
        public string DebugInfo(VehicleSync v)
        {
            var f = v.Frame;
            Vector3 vis = targetInside && f != null ? f.TransformPoint(targetPos) : targetPos;
            return $"partner vis={vis} inside={targetInside} root={PartnerRootWorld(v)} avatar={(Exists ? root.transform.position.ToString() : "none")} active={(Exists && root.activeSelf)} age={Time.time - LastUpdateTime:0.0}s";
        }

        // ---------------- sending (local player) ----------------

        private Animator localAnimCache;
        private AnimatorControllerParameter[] localParamsCache;

        public byte[] BuildLocalPlayerMessage(VehicleSync vehicle, bool paused)
        {
            var ch = GameAccess.LocalCharacter;
            if (ch == null) return null;

            var animator = GameAccess.CharacterAnimator(ch);
            Transform visual = animator != null ? animator.transform : ch.transform;

            bool inside = GameAccess.CharacterInsideVehicle(ch);
            Transform frame = vehicle.Frame;
            if (frame == null) inside = false;

            byte flags = 0;
            if (inside) flags |= FlagInsideVehicle;
            if (animator != null) flags |= FlagHasAnim;

            var w = Proto.Begin(MsgType.Player, out var ms);
            w.Write(flags);
            if (inside)
            {
                w.Write(frame.InverseTransformPoint(visual.position));
                w.Write(Quaternion.Inverse(frame.rotation) * visual.rotation);
            }
            else
            {
                w.Write(visual.position);
                w.Write(visual.rotation);
            }
            w.Write(visual.lossyScale);

            if (animator != null)
            {
                if (localAnimCache == null || localAnimCache.Pointer != animator.Pointer)
                {
                    localAnimCache = animator;
                    localParamsCache = ReadParams(animator);
                }
                int hash = 0; float t = 0f;
                try
                {
                    var info = animator.GetCurrentAnimatorStateInfo(0);
                    hash = info.fullPathHash;
                    t = info.normalizedTime;
                }
                catch { }
                w.Write(hash);
                w.Write(t);

                var ps = localParamsCache ?? new AnimatorControllerParameter[0];
                w.Write((byte)Math.Min(ps.Length, 255));
                for (int i = 0; i < ps.Length && i < 255; i++)
                {
                    float v = 0f;
                    try
                    {
                        switch (ps[i].type)
                        {
                            case AnimatorControllerParameterType.Float: v = animator.GetFloat(ps[i].nameHash); break;
                            case AnimatorControllerParameterType.Int: v = animator.GetInteger(ps[i].nameHash); break;
                            case AnimatorControllerParameterType.Bool: v = animator.GetBool(ps[i].nameHash) ? 1f : 0f; break;
                        }
                    }
                    catch { }
                    w.Write(v);
                }
            }

            // v3 extension: character root position (same frame as above), input, pause state
            Vector3 root = ch.transform.position;
            w.Write(inside ? frame.InverseTransformPoint(root) : root);
            var input = PartnerInput.ReadLocal();
            w.Write(input.Horizontal);
            w.Write(input.Vertical);
            byte b = 0;
            if (input.Jump) b |= 1;
            if (input.Take) b |= 2;
            if (paused) b |= 4;
            w.Write(b);
            w.Write(input.JumpPresses);
            w.Write(input.TakePresses);
            Vector3 look = Vector3.zero;
            try { look = ch.GetLookDirection(); } catch { }
            w.Write(look);
            return Proto.End(w, ms);
        }

        private static AnimatorControllerParameter[] ReadParams(Animator a)
        {
            try
            {
                var arr = a.parameters;
                var list = new AnimatorControllerParameter[arr.Length];
                for (int i = 0; i < arr.Length; i++) list[i] = arr[i];
                return list;
            }
            catch { return new AnimatorControllerParameter[0]; }
        }

        // ---------------- receiving (partner) ----------------

        public void ReadPlayerMessage(BinaryReader r)
        {
            byte flags = r.ReadByte();
            targetInside = (flags & FlagInsideVehicle) != 0;
            targetPos = r.ReadVector3();
            targetRot = r.ReadQuaternion();
            targetScale = r.ReadVector3();
            if ((flags & FlagHasAnim) != 0)
            {
                targetStateHash = r.ReadInt32();
                targetStateTime = r.ReadSingle();
                int n = r.ReadByte();
                if (targetParams == null || targetParams.Length != n) targetParams = new float[n];
                for (int i = 0; i < n; i++) targetParams[i] = r.ReadSingle();
            }
            if (r.BaseStream.Position < r.BaseStream.Length)
            {
                Vector3 newRoot = r.ReadVector3();
                float dtMsg = Time.time - LastUpdateTime;
                if (hasRoot && dtMsg > 0.005f && dtMsg < 0.5f)
                {
                    Vector3 v = (newRoot - targetRoot) / dtMsg;
                    rootVelocity = v.sqrMagnitude < 400f ? Vector3.Lerp(rootVelocity, v, 0.5f) : Vector3.zero;
                }
                targetRoot = newRoot;
                var inp = new RemoteInput
                {
                    Horizontal = r.ReadSingle(),
                    Vertical = r.ReadSingle(),
                };
                byte b = r.ReadByte();
                inp.Jump = (b & 1) != 0;
                inp.Take = (b & 2) != 0;
                PartnerPaused = (b & 4) != 0;
                inp.JumpPresses = r.ReadByte();
                inp.TakePresses = r.ReadByte();
                Input = inp;
                hasRoot = true;
                if (r.BaseStream.Position + 12 <= r.BaseStream.Length)
                {
                    Vector3 look = r.ReadVector3();
                    look.y = 0f;
                    if (look.sqrMagnitude > 0.01f) lookDir = look.normalized;
                }
                if (lookDir.sqrMagnitude < 0.01f && Mathf.Abs(inp.Horizontal) > 0.2f)
                    lookDir = new Vector3(Mathf.Sign(inp.Horizontal), 0f, 0f);   // fallback: walking direction
            }
            hasTarget = true;
            LastUpdateTime = Time.time;
        }

        private Vector3 targetRoot;
        private Vector3 rootToVisual;
        private Vector3 meshOffset = new Vector3(0f, 0.9f, 0f);  // local character: mesh center - root
        private Vector3 cloneCenterLocal;                        // copy: mesh center in avatar space
        private Renderer avatarRenderer;
        private float nextAlign;
        private bool alignLogged;
        private int alignLogCount;

        private void UpdateAlignment()
        {
            if (Time.unscaledTime < nextAlign) return;
            nextAlign = Time.unscaledTime + 0.5f;
            try
            {
                var ch = GameAccess.LocalCharacter;
                Renderer lr = null;
                try { lr = ch?.meshCharacter; } catch { }
                if (ch != null && GameAccess.Alive(lr))
                {
                    Vector3 off = lr.bounds.center - ch.transform.position;
                    if (off.sqrMagnitude < 9f) meshOffset = off;
                }
                if (GameAccess.Alive(avatarRenderer))
                {
                    Vector3 c = root.transform.InverseTransformPoint(avatarRenderer.bounds.center);
                    if (c.sqrMagnitude < 1e10f) cloneCenterLocal = c;
                }
                if (alignLogCount < 4 && GameAccess.Alive(avatarRenderer))
                {
                    alignLogCount++;
                    string orig = "-";
                    try
                    {
                        var ch2 = GameAccess.LocalCharacter;
                        var mr = ch2.meshCharacter;
                        orig = $"{mr.name} type={mr.GetIl2CppType().Name} pos={mr.transform.position} bounds={mr.bounds.center}/{mr.bounds.size} layer={mr.gameObject.layer} visible={mr.isVisible} enabled={mr.enabled}";
                    }
                    catch (Exception e) { orig = "err " + e.Message; }
                    var ar = avatarRenderer;
                    string mine = $"{ar.name} type={ar.GetIl2CppType().Name} pos={ar.transform.position} bounds={ar.bounds.center}/{ar.bounds.size} layer={ar.gameObject.layer} visible={ar.isVisible} enabled={ar.enabled} active={ar.gameObject.activeInHierarchy} root={root.transform.position} scale={ar.transform.lossyScale}";
                    int rc = 0;
                    try { rc = model.gameObject.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true).Length; } catch { }
                    FarCoopPlugin.Logger.LogInfo($"Avatar align: meshOffset={meshOffset} cloneCenterLocal={cloneCenterLocal} renderers={rc}\n  ORIG {orig}\n  COPY {mine}");
                }
            }
            catch { }
        }
        private Vector3 lookDir;
        private Vector3 rootVelocity;        // where the partner is looking / walking   // measured once on our own character (same model)
        private bool hasRoot;
        public RemoteInput Input { get; private set; }
        public bool PartnerPaused { get; private set; }

        /// <summary>Partner's character root in world space (null if unknown).</summary>
        public Vector3? PartnerRootWorld(VehicleSync vehicle)
        {
            if (!hasRoot) return null;
            if (!targetInside) return targetRoot;
            var f = vehicle.Frame;
            if (f == null) return null;
            return f.TransformPoint(targetRoot);
        }

        /// <summary>Hide/show the visual-only avatar (hidden on the host while the real puppet exists).</summary>
        public bool Suppressed { get; set; }

        private Vector3 smoothedLocal;
        private Quaternion smoothedLocalRot = Quaternion.identity;
        private bool smoothedInside;

        public void Update(VehicleSync vehicle, float dt)
        {
            if (!hasTarget) return;
            if (Suppressed) { if (Exists) Destroy(); return; }
            if (!Exists)
            {
                if (Time.unscaledTime < nextBuildAttempt) return;
                nextBuildAttempt = Time.unscaledTime + 2f;
                Build();
                if (!Exists) return;
            }

            Vector3 worldPos = targetPos;
            Quaternion worldRot = targetRot;
            if (targetInside)
            {
                var frame = vehicle.Frame;
                if (frame == null) return; // wait for the vehicle
                worldPos = frame.TransformPoint(targetPos);
                worldRot = frame.rotation * targetRot;
            }
            // FAR's visual body is interpolated separately and can report odd positions:
            // stand the avatar on the partner's physics root instead.
            var t = root.transform;
            var frameT = targetInside ? vehicle.Frame : null;
            var rootWorld = PartnerRootWorld(vehicle);
            if (rootWorld.HasValue)
            {
                float ahead = Mathf.Clamp(Time.time - LastUpdateTime, 0f, 0.1f);
                // walking speed is measured in the vehicle's frame while inside it
                Vector3 vel = frameT != null ? frameT.TransformVector(rootVelocity) : rootVelocity;
                // FAR's model is skinned far away from its own transform: align the MESH CENTER of
                // the copy with where the mesh sits on a real character (root + meshOffset).
                UpdateAlignment();
                Vector3 desiredCenter = rootWorld.Value + meshOffset + vel * ahead;
                worldPos = desiredCenter - t.rotation * cloneCenterLocal;
            }

            float k = 1f - Mathf.Exp(-25f * dt);
            if (frameT != null)
            {
                // smooth in the vehicle's own space: the avatar rides along exactly with the
                // vehicle instead of trailing behind it (that looked like sliding)
                Vector3 localTarget = frameT.InverseTransformPoint(worldPos);
                Quaternion localRotTarget = Quaternion.Inverse(frameT.rotation) * worldRot;
                if (!smoothedInside || (smoothedLocal - localTarget).sqrMagnitude > 25f)
                {
                    smoothedLocal = localTarget; smoothedLocalRot = localRotTarget; smoothedInside = true;
                }
                else
                {
                    smoothedLocal = Vector3.Lerp(smoothedLocal, localTarget, k);
                    smoothedLocalRot = Quaternion.Slerp(smoothedLocalRot, localRotTarget, k);
                }
                Vector3 sp = frameT.TransformPoint(smoothedLocal);
                sp.z = worldPos.z;   // FAR is a side view: never let smoothing push the body out of its depth plane
                t.position = sp;
                t.rotation = frameT.rotation * smoothedLocalRot;
            }
            else
            {
                smoothedInside = false;
                if ((t.position - worldPos).sqrMagnitude > 25f) t.position = worldPos;
                else t.position = Vector3.Lerp(t.position, worldPos, k);
                t.rotation = Quaternion.Slerp(t.rotation, worldRot, k);
            }
            if (model != null && !isCapsule) model.localScale = targetScale;

            // hide the avatar if the partner stopped sending (menu, loading)
            bool visible = Time.time - LastUpdateTime < 3f;
            if (root.activeSelf != visible) root.SetActive(visible);

            ApplyAnimator();
        }

        private void ApplyAnimator()
        {
            if (anim == null || !GameAccess.Alive(anim)) return;
            try
            {
                if (targetParams != null && animParams != null && animParams.Length == targetParams.Length)
                {
                    for (int i = 0; i < animParams.Length; i++)
                    {
                        var p = animParams[i];
                        switch (p.type)
                        {
                            case AnimatorControllerParameterType.Float: anim.SetFloat(p.nameHash, targetParams[i]); break;
                            case AnimatorControllerParameterType.Int: anim.SetInteger(p.nameHash, Mathf.RoundToInt(targetParams[i])); break;
                            case AnimatorControllerParameterType.Bool: anim.SetBool(p.nameHash, targetParams[i] > 0.5f); break;
                        }
                    }
                }

                if (targetStateHash != 0)
                {
                    var cur = anim.GetCurrentAnimatorStateInfo(0);
                    if (cur.fullPathHash != targetStateHash && !anim.IsInTransition(0))
                        anim.CrossFade(targetStateHash, 0.1f, 0, targetStateTime - Mathf.Floor(targetStateTime));
                }
            }
            catch { }
        }

        // ---------------- building ----------------

        private void Build()
        {
            Destroy();
            try
            {
                var ch = GameAccess.LocalCharacter;
                GameObject source = null;
                var srcAnim = GameAccess.CharacterAnimator(ch);
                if (srcAnim != null) source = srcAnim.gameObject;
                if (source == null && ch != null)
                {
                    try { if (GameAccess.Alive(ch.meshCharacter)) source = ch.meshCharacter.gameObject; } catch { }
                }

                try
                {
                    if (ch != null && srcAnim != null)
                    {
                        Vector3 off = srcAnim.transform.position - ch.transform.position;
                        rootToVisual = off.sqrMagnitude < 4f ? off : Vector3.zero;   // ignore garbage offsets
                    }
                }
                catch { rootToVisual = Vector3.zero; }
                root = new GameObject("FarCoop_Partner");
                if (source != null) model = CloneVisual(source, root.transform);
                isCapsule = model == null;
                if (model == null) model = MakeCapsule(root.transform);

                // the copy must be visible even if the source mesh was hidden at that moment
                try
                {
                    var rs = model.gameObject.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                    for (int i = 0; i < rs.Length; i++)
                    {
                        var rr = rs[i]?.TryCast<Renderer>();
                        if (rr == null) continue;
                        if (!rr.gameObject.activeSelf) rr.gameObject.SetActive(true);
                        rr.enabled = true;
                    }
                }
                catch { }

                // the biggest renderer of the copy = its body mesh; keep it animating off-screen
                avatarRenderer = null; alignLogged = false; alignLogCount = 0; nextAlign = 0f; cloneCenterLocal = Vector3.zero;
                try
                {
                    var rs = model.gameObject.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                    float best = -1f;
                    for (int i = 0; i < rs.Length; i++)
                    {
                        var rr = rs[i]?.TryCast<Renderer>();
                        if (rr == null) continue;
                        var smr = rr.TryCast<SkinnedMeshRenderer>();
                        if (smr != null) smr.updateWhenOffscreen = true;
                        float size = rr.bounds.size.sqrMagnitude;
                        if (size > best) { best = size; avatarRenderer = rr; }
                    }
                }
                catch { }

                anim = GameAccess.GetComp<Animator>(model.gameObject);
                if (anim != null)
                {
                    try { anim.applyRootMotion = false; } catch { }
                    try { anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; } catch { }
                    animParams = ReadParams(anim);
                }
                FarCoopPlugin.Logger.LogInfo("Partner avatar created" + (anim != null ? " (animated)" : " (static)"));
            }
            catch (Exception e)
            {
                FarCoopPlugin.Logger.LogWarning("Partner avatar build failed: " + e.Message);
                Destroy();
                try
                {
                    root = new GameObject("FarCoop_Partner");
                    model = MakeCapsule(root.transform);
                    isCapsule = true;
                }
                catch { }
            }
        }

        private static readonly HashSet<string> KeepTypes = new HashSet<string>
        {
            "Transform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "Animator", "LODGroup"
        };

        internal static Transform CloneVisual(GameObject source, Transform parent)
        {
            // Clone under an inactive holder so none of the game's scripts ever Awake/Start.
            var holder = new GameObject("FarCoop_Holder");
            holder.SetActive(false);
            GameObject clone = null;
            try
            {
                var obj = UnityEngine.Object.Instantiate((UnityEngine.Object)source, holder.transform);
                clone = obj == null ? null : obj.TryCast<GameObject>();
                if (clone == null) return null;
                clone.name = "Model";

                for (int pass = 0; pass < 4; pass++)
                {
                    var comps = clone.GetComponentsInChildren(Il2CppType.Of<Component>(), true);
                    int remaining = 0;
                    for (int i = 0; i < comps.Length; i++)
                    {
                        var c = comps[i];
                        if (c == null) continue;
                        string typeName;
                        try { typeName = c.GetIl2CppType().Name; } catch { continue; }
                        if (KeepTypes.Contains(typeName)) continue;
                        // MonoBehaviours first, physics/others in later passes (RequireComponent order)
                        bool isScript = c.TryCast<MonoBehaviour>() != null;
                        if (pass == 0 && !isScript) { remaining++; continue; }
                        try { UnityEngine.Object.DestroyImmediate(c); } catch { remaining++; }
                    }
                    if (pass > 0 && remaining == 0) break;
                }

                clone.transform.SetParent(parent, false);
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.SetActive(true);
                return clone.transform;
            }
            catch (Exception e)
            {
                FarCoopPlugin.Logger.LogWarning("Clone of character model failed: " + e.Message);
                if (clone != null) try { UnityEngine.Object.Destroy(clone); } catch { }
                return null;
            }
            finally
            {
                try { UnityEngine.Object.Destroy(holder); } catch { }
            }
        }

        private static Transform MakeCapsule(Transform parent)
        {
            var cap = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            var col = cap.GetComponent(Il2CppType.Of<Collider>());
            if (col != null) UnityEngine.Object.DestroyImmediate(col);
            cap.name = "Model";
            cap.transform.SetParent(parent, false);
            cap.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            cap.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
            return cap.transform;
        }

        public void Destroy()
        {
            if (GameAccess.Alive(root)) try { UnityEngine.Object.Destroy(root); } catch { }
            root = null; model = null; anim = null; animParams = null;
        }

        public void Reset()
        {
            Destroy();
            hasTarget = false;
            hasRoot = false;
            PartnerPaused = false;
        }
    }
}
