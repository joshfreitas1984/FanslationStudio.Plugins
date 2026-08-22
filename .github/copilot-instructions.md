# Copilot Instructions — FanslationStudio.Plugins

## Project shape

- This is a **generic, reusable set of plugins** for fan-translating Unity games, not built for a
  single title. The same plugin code is tested against multiple different games (different
  Unity versions, different IL2CPP/Mono builds, different obfuscation/stripping levels). A fix or
  workaround that's needed for one game may not be needed (or may not even apply) to another, and
  a pattern confirmed "safe" against one game's build should not be assumed safe for all of them.
  When troubleshooting a crash, keep in mind which specific game/build the repro came from, and
  prefer defensive coding (try/catch around interop calls, fallbacks) over hard-coding around one
  game's quirks where practical.
- `FanslationStudio.Plugins.Shared` (netstandard2.1): host-agnostic plugin services (e.g.

  `TextResizerService`, `SpriteReplacerService`). Compiled against **Mono-style stub reference
  assemblies** in `Reference\` (e.g. `Reference\UnityEngine.dll`), NOT the target game's real
  assemblies. This is compile-time only — these stubs can declare APIs/overloads that do not
  actually exist in a given game's real IL2CPP build. Don't trust the stub's API surface as proof
  a method exists at runtime.
- `FanslationStudio.Plugins.BepInEx5` (Mono/BepInEx5 host): works reliably. Most interop issues
  below are IL2CPP-specific and have **not** been observed on this host.
- `FanslationStudio.Plugins.BepInEx6.IL2CPP` (BepInEx 6 IL2CPP host, currently on
  `BepInEx.Unity.IL2CPP` `6.0.0-be.785`, a prerelease build): compiled against the game's own
  unhollowed assemblies in `unhollowed\`. This host is where all the issues below occur.

## Working hypothesis: this may simply be an IL2CPP/Il2CppInterop instability, not a code bug

Everything below reproduces only in the IL2CPP host. The equivalent Mono/BepInEx5 build works
fine with the "normal" patterns (AddComponent<T>, generic GetComponentsInChildren<T>, etc). This
suggests the root problem is with `Il2CppInterop`/BepInEx6 prerelease `be.785` generic-method
support in this specific game, not with the plugin architecture itself. Before spending more time
finding workarounds, consider:

- Trying a different `BepInEx.Unity.IL2CPP` `6.0.0-be.*` version (earlier or later) to see if the
  crash disappears — classic sign of an interop regression in a specific build.
- Checking BepInEx/Il2CppInterop GitHub issues for `GenericMethod_GetMethod_Hook`
  `AccessViolationException` reports around this build.

## Confirmed-unsafe patterns under this IL2CPP host (all crash or throw)

Do **not** reintroduce these without first re-verifying against a newer/older Il2CppInterop build:

1. **Any generic Il2Cpp interop call made synchronously inside `BasePlugin.Load()`.**
   `Load()` runs nested inside the chainloader's own `il2cpp_runtime_invoke` →
   `OnInvokeMethod` call. Making another generic interop call inside it reenters
   `Il2CppInterop.Runtime.Injection.Hooks.GenericMethod_GetMethod_Hook`, corrupting shared state
   and crashing with `System.AccessViolationException`. Confirmed culprits:
   - `ClassInjector.RegisterTypeInIl2Cpp<T>()` + `AddComponent<T>()` called from `Load()`.
   - `SomeIl2CppDelegateType.op_Implicit(System.Action)` (e.g.
	 `Canvas.willRenderCanvases += (Canvas.WillRenderCanvases)SomeMethod;`) — the delegate
	 conversion goes through `DelegateSupport.ConvertDelegate` → `Type.GetMethod`, which reenters
	 the same hook.
2. **`ClassInjector.RegisterTypeInIl2Cpp<T>()` even from a later, non-`Load()` call site**
   (e.g. from inside a Harmony postfix triggered during normal gameplay). Also crashed. This is
   why the working hypothesis above leans toward "IL2CPP build instability" rather than "just a
   timing issue" — no call site we tried for these generic calls was safe.
3. **Wrapping a native interop call inside a generic helper method**, even when the interop call
   itself is non-generic. E.g. a helper like
   `IEnumerable<T> GetComponentsInChildrenSafe<T>(GameObject go, bool includeInactive) where T : Component`
   that internally calls `go.GetComponentsInChildren(typeof(T), includeInactive)` still throws
   `MissingMethodException` at runtime, because IL2CPP can't resolve the native call from within
   shared/generic method body code. The call must be inlined directly, non-generically, at each
   use site.
4. **CONFIRMED ROOT CAUSE (superseding earlier speculation in this section): any Unity API call
   made from code compiled in `FanslationStudio.Plugins.Shared` throws `MissingMethodException`
   at runtime under IL2CPP, even for APIs that verifiably exist in the game's real assembly.**
   `Shared` is compiled against the Mono-style stub assemblies in `Reference\` (e.g.
   `Reference\UnityEngine.dll`) — a physically different compiled assembly from the game's real
   `unhollowed\UnityEngine.CoreModule.dll` (same type/namespace names, different manifest).
   Il2CppInterop's native dispatch trampolines are wired into the specific unhollowed DLL at
   generation time; when IL compiled against the stub calls a Unity API, interop can't resolve
   the call against its trampoline table, even though a same-named/signatured method genuinely
   exists in the real assembly. Confirmed via runtime reflection dump (see Debugging tips below):
   `Component.GetComponent(Type)`, `GameObject.GetComponentsInChildren(Type, bool)`, and
   `Object.FindObjectsOfType(Type)` **all exist** in this game's real assembly, yet all three
   threw `MissingMethodException` when called from `TextResizerService.cs` (in `Shared`).
   This is the same reason `TextMetadataComponents.cs` already lives in the
   `BepInEx6.IL2CPP` host project instead of `Shared` — but the rule is broader than just custom
   `MonoBehaviour`s: **any code that calls Unity/engine API under the IL2CPP host must be
   compiled directly in the IL2CPP host project (against the real `unhollowed\` assemblies), not
   in `Shared`.** Where `Shared` needs this behavior, either move the method to the host project,
   or take it as a dependency-injected delegate/interface implemented in the host project (see
   `IBehaviourAttacher`/`Il2CppBehaviourAttacher` for the existing pattern).
5. **Directly subscribing a method group to `SceneManager.sceneLoaded`** fails under IL2CPP with
   a `MissingMethodException`, because the interop-generated `UnityAction<T1,T2>` type doesn't
   support the standard `(object, IntPtr)` delegate constructor the C# compiler emits for a method
   group. (Documented in `TextResizerService`'s `_lastSceneBuildIndex` polling comment — use
   polling instead of subscribing.)

## Confirmed-safe pattern for per-frame ticking under IL2CPP (no MonoBehaviour needed)

`BasePlugin` is a plain C# class — Unity never calls `Update()` on it. Instead of
`AddComponent<T>`/`ClassInjector` (unsafe, see above), use a Harmony postfix patch on a concrete,
non-generic engine method/property that's called every frame by native code. Harmony patch
application only needs ordinary `MethodInfo` resolution + an IL detour — it does not go through
`GenericMethod_GetMethod_Hook`. Current implementation (`TextResizerPlugin.Load()`): patch
`UnityEngine.Time.deltaTime`'s getter with a postfix, throttled to once per frame via
`Time.frameCount` (since `deltaTime` may be read many times per frame by other game code).

## Debugging tips specific to this repo

- To inspect the real available API surface of the game's unhollowed assemblies (not the Mono
  reference stubs), load the DLL under `unhollowed\` via reflection in a small PowerShell script
  and catch `ReflectionTypeLoadException` (partial type load is common in unhollowed dumps):
  ```powershell
  $asm = [System.Reflection.Assembly]::LoadFile($path)
  try { $allTypes = $asm.GetTypes() }
  catch [System.Reflection.ReflectionTypeLoadException] { $allTypes = $_.Exception.Types | Where-Object { $_ -ne $null } }
  ```
- Don't assume a plugin pattern is "proven" just because another plugin in this repo
  (`SpriteReplacerPlugin`, `StringPatcherPlugin`) uses it — those may not have actually been
  exercised through this crash path. Verify empirically before citing precedent.
- A safe, zero-risk way to discover the real API surface without any of the above: use ordinary
  **`.NET reflection`** (`type.GetMethods()`, no Il2Cpp interop invocation at all) from code
  compiled directly in the `BepInEx6.IL2CPP` host project (so `typeof(Component)` etc. resolve to
  the real unhollowed types) and log the results at plugin `Load()`/first tick. This is how item
  4 above was actually confirmed — it's safe because it never invokes the native method, it only
  inspects metadata.
- **Resolved**: `ForEachComponentInChildren` and the `GameObject.SetActive`/`CanvasGroup.alpha`
  Harmony postfixes that used it were moved out of `TextResizerService.cs` (`Shared`) into
  `TextResizerGameObjectPatches.cs` in the `BepInEx6.IL2CPP` host project. The tree-walk there uses
  `transform.GetComponent(Il2CppType.From(componentType))` — the real unhollowed `GetComponent`
  overload takes `Il2CppSystem.Type`, not `System.Type`, so `Il2CppType.From(...)` is required to
  convert. This patch class has its own deferred `EnsurePatched()` (same timing rule as
  `TextResizerService.EnsurePatched()` — must be called after the first frame, not from `Load()`),
  called from `TextResizerPlugin.RunUpdate()` alongside the existing service patch. The simpler
  postfixes that only call `ApplyResizing`/`ApplyResizingToLegacyText` directly (no
  `GetComponent`/tree-walk) remain in `Shared` since they don't touch the problematic APIs.
- **Follow-up bug found in the above fix**: `Il2CppType.From(componentType)`-based
  `transform.GetComponent(...)` has been observed to occasionally return a `Component` that is
  *not* actually an instance of the requested type (`InvalidCastException` at the postfix's
  callback site, e.g. casting to `Text` when the real returned object isn't one), i.e. the
  Il2Cpp-type-filtered overload doesn't reliably enforce its own type filter in this game's build.
  `ForEachComponentInChildren` now guards with a plain managed `componentType.IsInstanceOfType(component)`
  check before invoking the callback, skipping (rather than crashing on) a mismatched result.
- **Resolved**: `TextResizerService.FindAllTextElements()`/`FindAllLegacyTextElements()` called
  the generic `UnityEngine.Object.FindObjectsOfType<T>()` from `Shared`, throwing
  `MissingMethodException` at runtime under IL2CPP (method not found:
  `'!!0[] UnityEngine.Object.FindObjectsOfType()'`) for the same root-cause reason as item 4.
  Added `FindAllTextElements()`/`FindAllLegacyTextElements()` to `IBehaviourAttacher` and
  implemented them in all three host attachers (`BepInEx5`/`BepInEx6` Mono attachers just call
  `FindObjectsOfType<T>()` directly since those hosts compile against the real assembly;
  `Il2CppBehaviourAttacher` in `BepInEx6.IL2CPP` does the same but only works because it's
  compiled in the host project). `TextResizerService` now delegates to `_behaviourAttacher`
  instead of calling `FindObjectsOfType<T>()` itself. **User-confirmed working in-game.**

## General rule of thumb: audit every plugin, not just TextResizer

The `Shared`-vs-host assembly mismatch (item 4) applies to *any* plugin/service in `Shared`, not
just `TextResizerService`. When adding or reviewing a `Shared` service, grep it for these Unity
API call patterns before assuming it's safe under IL2CPP - all of them must be moved to (or
delegated via an `I...Attacher`-style interface to) a host project compiled against the real
`unhollowed\` assemblies:
- `UnityEngine.Object.FindObjectsOfType<T>()` / `FindObjectOfType<T>()` (generic)
- `Resources.FindObjectsOfTypeAll(Type)` (non-generic, but still Type-based interop - same
  failure category as the `GetComponentsInChildren(Type, bool)` case in item 3/4)
- `gameObject.GetComponentsInChildren(Type, bool)` / `GetComponentInChildren(Type)` and their
  generic `<T>` equivalents
- `component.GetComponent(Type)` / `GetComponent<T>()`, `AddComponent<T>()`
- `GameObject.FindGameObjectWithTag`/`FindGameObjectsWithTag` (untested here, but same category -
  treat as suspect until proven safe)
- Any `MonoBehaviour`-derived class meant to be attached via `AddComponent<T>`/`GetComponent<T>`
  (needs a concrete IL2CPP-compatible type with an `(IntPtr)` constructor, defined in the host
  project - see `TextMetadataComponents.cs` for the template)

Known instances still open (found by repo-wide grep, not yet fixed) as of this note:
- ~~`TextResizerService.FindTextElementsUnderCursor()` / `FindLegacyTextElementsUnderCursor()`~~
  **Resolved**: both now call `FindAllTextElements()`/`FindAllLegacyTextElements()` (which already
  delegate to `_behaviourAttacher`) instead of calling `FindObjectsOfType<T>()` directly.
- ~~`FanslationStudio.Plugins.Shared\Sprites\SpriteReplacerService.cs`~~ **Resolved**: added
  `ISpriteElementFinder` (mirrors `IBehaviourAttacher`) with implementations in all three hosts
  (`MonoSpriteElementFinder` in `BepInEx5`/`BepInEx6`, `Il2CppSpriteElementFinder` in
  `BepInEx6.IL2CPP`). `FindAllElements()`/`FindElementsAtCursor()` now delegate to it instead of
  calling `UnityEngine.Object.FindObjectsOfType<Image>()` directly. The `Postfix_GameObject_SetActive`
  postfix (which used `__instance.GetComponentsInChildren(typeof(Image), false)`) was moved out of
  `Shared` into a new `SpriteReplacerGameObjectPatches.cs` in the `BepInEx6.IL2CPP` host project,
  following the same pattern as `TextResizerGameObjectPatches.cs` (uses
  `Il2CppType.From(typeof(Image))` for the type-filtered `GetComponentsInChildren` call), with its
  own deferred `EnsurePatched()` called from `SpriteReplacerPlugin.RunUpdate()`.
- `FanslationStudio.Plugins.Shared\PrefabText\PrefabTextDumperService.cs` -
  `Resources.FindObjectsOfTypeAll(typeof(GameObject))` and
  `gameObject.GetComponentsInChildren(typeof(Component), true)`. **Dormant, not actively fixed**:
  its only consumer, `BepInEx5\Plugins\PrefabTextDumperPlugin.cs`, is entirely commented out (dead
  code) - no host project currently constructs this service, so there's no live crash path and no
  way to test a fix in-game. Fix this the same way as `SpriteReplacerService` (per-host finder
  interface) if/when this plugin is re-enabled.
- `FanslationStudio.Plugins.Shared\TextResizer\TextChangedBehaviour.cs` -
  `GetComponent<TextMeshProUGUI>()` inside a `MonoBehaviour`-derived class living in `Shared`.
  **Dormant, not actively fixed**: the only references to this class are inside fully
  commented-out code in `TextResizerService.cs` (`AddTextElementsToResizers`) - it's never
  actually attached via `AddComponent`/`GetComponent` anywhere reachable. Note for later: even if
  reactivated, an *instance* `GetComponent<T>()` call from within an already-attached
  `MonoBehaviour`'s own `Awake()` may not hit the same failure mode as the static/Type-based
  interop lookups documented above (it wasn't empirically tested here) - but the class itself
  would still need an IL2CPP-specific subclass (like `TextMetadataComponent`) to be attachable at
  all under IL2CPP, since it's a plain `MonoBehaviour` with no `(IntPtr)` constructor.

