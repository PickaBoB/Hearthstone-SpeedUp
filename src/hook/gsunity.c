/*
 * gsunity.dll -- sets UnityEngine.Time.timeScale from inside the game.
 *
 * Why this exists alongside gsphook.dll: Unity clamps Time.deltaTime to
 * Time.maximumDeltaTime (1/3 s by default), so feeding the engine a faster
 * OS clock cannot push the scaled part of the engine past roughly x10-x20.
 * timeScale multiplies inside the engine, past that clamp, so x30 really is
 * x30 and everything on scaled time stays in step.
 *
 * Reaching timeScale means calling into the scripting runtime. Both backends
 * export an embedding API that can look a class up by name and invoke a
 * method, so one code path covers them:
 *
 *   Mono    mono-2.0-bdwgc.dll / mono.dll   mono_*
 *   IL2CPP  GameAssembly.dll                il2cpp_*
 *
 * Like gsphook, this hides from nothing: the module is visible in the module
 * list and the calls are the runtime's own public embedding API.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdarg.h>

/* ------------------------------------------------------------------ */
/* Logging                                                             */
/* ------------------------------------------------------------------ */

#ifdef GSP_NO_LOG
#define gsp_log(fmt, ...) ((void)0)
#else
static void gsp_log(const char *fmt, ...)
{
    char path[MAX_PATH], line[1024], tmp[MAX_PATH];
    HANDLE h;
    DWORD written, n;
    va_list ap;

    if (!GetTempPathA(sizeof(tmp), tmp)) return;
    wsprintfA(path, "%sgsunity_%lu.log", tmp, GetCurrentProcessId());

    va_start(ap, fmt);
    n = (DWORD)wvsprintfA(line, fmt, ap);
    va_end(ap);
    if (n + 2 >= sizeof(line)) return;
    line[n++] = '\r';
    line[n++] = '\n';

    h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    WriteFile(h, line, n, &written, NULL);
    CloseHandle(h);
}
#endif

/* ------------------------------------------------------------------ */
/* Shared control block -- layout must match SharedCtl.cs exactly.     */
/* ------------------------------------------------------------------ */

#define GSP_MAGIC   0x44505347u   /* "GSPD" */

#define BACKEND_NONE   0u
#define BACKEND_MONO   1u
#define BACKEND_IL2CPP 2u
#define BACKEND_CLOCK  3u

/* state: 0 = nothing reported yet, 1 = running, 2 = loaded but gave up.
 * The GUI needs 2 to tell "no Unity here" apart from "still starting up". */
#define STATE_RUNNING  1u
#define STATE_GAVE_UP  2u

#define FLAG_SELECTIVE 4u   /* Hearthstone: speed up only while actions play */

/* The `patched` field, which only gsphook uses for its own purpose, reports
 * the Hearthstone integration here. */
#define HS_ACTIVE      1u   /* going through TimeScaleMgr */
#define HS_SELECTIVE   2u   /* selective mode in force */
#define HS_FAST        4u   /* currently in a sped-up stretch */

#pragma pack(push, 1)
typedef struct {
    UINT32 magic;
    UINT32 version;
    double mult;
    UINT32 flags;
    UINT32 seq;
    UINT32 patched;
    UINT32 state;
    double actual;
    UINT32 backend;
    float  base_fixed;      /* fixedDeltaTime before any scaling; 0 = unknown */
    volatile LONG owner;    /* generation of the gsunity instance in charge   */
} GspShared;
#pragma pack(pop)

static GspShared *g_shm;
static HANDLE     g_shm_handle;

static void open_shared(void)
{
    WCHAR name[64];
    wsprintfW(name, L"Local\\GameSpeedCtl_%lu", GetCurrentProcessId());
    g_shm_handle = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
    if (!g_shm_handle) return;
    g_shm = (GspShared*)MapViewOfFile(g_shm_handle, FILE_MAP_ALL_ACCESS,
                                      0, 0, sizeof(GspShared));
    if (g_shm && g_shm->magic != GSP_MAGIC) {
        UnmapViewOfFile(g_shm);
        g_shm = NULL;
    }
}

/* ------------------------------------------------------------------ */
/* Scripting runtime embedding API                                     */
/* ------------------------------------------------------------------ */

/* Note: these are cdecl, not WINAPI. On x86 that distinction matters --
 * declaring them __stdcall would corrupt the stack on the first call. */
typedef void* (*fn_root_domain)(void);
typedef void* (*fn_thread_attach)(void *domain);
typedef void* (*fn_assembly_open)(void *domain, const char *name);
typedef void* (*fn_assembly_image)(void *assembly);
typedef void* (*fn_class_from_name)(void *image, const char *ns, const char *name);
typedef void* (*fn_class_method)(void *klass, const char *name, int argc);
typedef void* (*fn_runtime_invoke)(void *method, void *obj, void **params, void **exc);
typedef void* (*fn_object_unbox)(void *obj);
typedef const char* (*fn_image_get_name)(void *image);
typedef void  (*fn_assembly_cb)(void *assembly, void *user);
typedef void  (*fn_assembly_foreach)(fn_assembly_cb cb, void *user);              /* Mono */
typedef void** (*fn_domain_get_assemblies)(void *domain, size_t *count);         /* IL2CPP */
typedef void* (*fn_field_from_name)(void *klass, const char *name);
typedef void  (*fn_field_get_value)(void *obj, void *field, void *out);
typedef void* (*fn_class_vtable)(void *domain, void *klass);                     /* Mono */
typedef void  (*fn_mono_static_get)(void *vtable, void *field, void *out);       /* Mono */
typedef void  (*fn_il2cpp_static_get)(void *field, void *out);                   /* IL2CPP */
typedef void* (*fn_object_isinst)(void *obj, void *klass);                       /* Mono */
typedef void* (*fn_object_get_class)(void *obj);                                 /* IL2CPP */
typedef BOOL  (*fn_class_assignable)(void *klass, void *oklass);                 /* IL2CPP */

static struct {
    HMODULE module;
    UINT32  backend;

    fn_root_domain     root_domain;
    fn_thread_attach   thread_attach;
    fn_assembly_open   assembly_open;
    fn_assembly_image  assembly_image;
    fn_class_from_name class_from_name;
    fn_class_method    class_method;
    fn_runtime_invoke  runtime_invoke;
    fn_object_unbox    object_unbox;
    fn_image_get_name  image_get_name;
    fn_assembly_foreach      assembly_foreach;
    fn_domain_get_assemblies domain_get_assemblies;
    fn_field_from_name   field_from_name;
    fn_field_get_value   field_get_value;
    fn_class_vtable      class_vtable;
    fn_mono_static_get   mono_static_get;
    fn_il2cpp_static_get il2cpp_static_get;
    fn_object_isinst     object_isinst;
    fn_object_get_class  object_get_class;
    fn_class_assignable  class_assignable;

    void *domain;
    void *set_timeScale;
    void *get_timeScale;
    void *set_fixedDeltaTime;
    void *get_fixedDeltaTime;

    float base_fixed_delta;   /* as the game shipped it, for scaling */
} rt;

/* Resolves one symbol under both naming schemes. */
static void *sym(const char *mono_name, const char *il2cpp_name)
{
    return (void*)GetProcAddress(rt.module,
        rt.backend == BACKEND_MONO ? mono_name : il2cpp_name);
}

/* A module that hosts a scripting runtime is identified by the embedding
 * export it offers, not by its file name. Name lists go stale: Unity has
 * shipped the Mono runtime as mono.dll, mono-2.0-bdwgc.dll and
 * mono-2.0-boehm.dll, games rename or repack it, and the injector's own
 * detection matched any mono*.dll -- so it could report Unity while this
 * side, checking a fixed list, gave up. Probing exports keeps the two in
 * agreement whatever the file is called. */
static BOOL probe_module(HMODULE m, const WCHAR *name)
{
    if (!m) return FALSE;

    if (GetProcAddress(m, "mono_get_root_domain")) {
        rt.module  = m;
        rt.backend = BACKEND_MONO;
        gsp_log("runtime: Mono in %S", name);
        return TRUE;
    }
    if (GetProcAddress(m, "il2cpp_domain_get")) {
        rt.module  = m;
        rt.backend = BACKEND_IL2CPP;
        gsp_log("runtime: IL2CPP in %S", name);
        return TRUE;
    }
    return FALSE;
}

/* When the runtime is not found, the one thing worth knowing is what engine
 * this actually is. Dumping the module list answers that from the log alone,
 * without another round of questions. */
static void dump_modules(void)
{
    HANDLE snap;
    MODULEENTRY32W me;
    int n = 0;

    snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32,
                                   GetCurrentProcessId());
    if (snap == INVALID_HANDLE_VALUE) {
        gsp_log("module list unavailable (error %lu)", GetLastError());
        return;
    }

    gsp_log("--- loaded modules, for identifying the engine ---");
    me.dwSize = sizeof(me);
    if (Module32FirstW(snap, &me)) {
        do {
            gsp_log("  %S", me.szModule);
            n++;
        } while (Module32NextW(snap, &me));
    }
    CloseHandle(snap);
    gsp_log("--- %d modules ---", n);
}

static BOOL find_runtime(void)
{
    /* The usual suspects first: no snapshot needed in the common case. */
    static const struct { const char *a; const WCHAR *w; } known[] = {
        { "mono-2.0-bdwgc.dll", L"mono-2.0-bdwgc.dll" },   /* Unity 2018+ */
        { "mono-2.0-boehm.dll", L"mono-2.0-boehm.dll" },
        { "mono.dll",           L"mono.dll"           },   /* older Unity */
        { "monobdwgc-2.0.dll",  L"monobdwgc-2.0.dll"  },
        { "GameAssembly.dll",   L"GameAssembly.dll"   },   /* IL2CPP */
    };
    HANDLE snap;
    MODULEENTRY32W me;
    size_t i;

    for (i = 0; i < sizeof(known) / sizeof(known[0]); i++)
        if (probe_module(GetModuleHandleA(known[i].a), known[i].w))
            return TRUE;

    /* Otherwise walk everything that is loaded and ask each module. */
    snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32,
                                   GetCurrentProcessId());
    if (snap == INVALID_HANDLE_VALUE) return FALSE;

    me.dwSize = sizeof(me);
    if (Module32FirstW(snap, &me)) {
        do {
            if (probe_module(me.hModule, me.szModule)) {
                CloseHandle(snap);
                return TRUE;
            }
        } while (Module32NextW(snap, &me));
    }
    CloseHandle(snap);
    return FALSE;   /* the caller logs, so a retry loop stays quiet */
}

static BOOL bind_api(void)
{
    rt.root_domain     = (fn_root_domain)    sym("mono_get_root_domain", "il2cpp_domain_get");
    rt.thread_attach   = (fn_thread_attach)  sym("mono_thread_attach", "il2cpp_thread_attach");
    rt.assembly_open   = (fn_assembly_open)  sym("mono_domain_assembly_open", "il2cpp_domain_assembly_open");
    rt.assembly_image  = (fn_assembly_image) sym("mono_assembly_get_image", "il2cpp_assembly_get_image");
    rt.class_from_name = (fn_class_from_name)sym("mono_class_from_name", "il2cpp_class_from_name");
    rt.class_method    = (fn_class_method)   sym("mono_class_get_method_from_name", "il2cpp_class_get_method_from_name");
    rt.runtime_invoke  = (fn_runtime_invoke) sym("mono_runtime_invoke", "il2cpp_runtime_invoke");
    rt.object_unbox    = (fn_object_unbox)   sym("mono_object_unbox", "il2cpp_object_unbox");
    rt.image_get_name  = (fn_image_get_name) sym("mono_image_get_name", "il2cpp_image_get_name");
    rt.field_from_name = (fn_field_from_name)sym("mono_class_get_field_from_name", "il2cpp_class_get_field_from_name");
    rt.field_get_value = (fn_field_get_value)sym("mono_field_get_value", "il2cpp_field_get_value");
    if (rt.backend == BACKEND_MONO) {
        rt.assembly_foreach = (fn_assembly_foreach)GetProcAddress(rt.module, "mono_assembly_foreach");
        rt.class_vtable     = (fn_class_vtable)GetProcAddress(rt.module, "mono_class_vtable");
        rt.mono_static_get  = (fn_mono_static_get)GetProcAddress(rt.module, "mono_field_static_get_value");
        rt.object_isinst    = (fn_object_isinst)GetProcAddress(rt.module, "mono_object_isinst");
        rt.object_get_class = (fn_object_get_class)GetProcAddress(rt.module, "mono_object_get_class");
    } else {
        rt.domain_get_assemblies = (fn_domain_get_assemblies)GetProcAddress(rt.module, "il2cpp_domain_get_assemblies");
        rt.il2cpp_static_get = (fn_il2cpp_static_get)GetProcAddress(rt.module, "il2cpp_field_static_get_value");
        rt.object_get_class  = (fn_object_get_class)GetProcAddress(rt.module, "il2cpp_object_get_class");
        rt.class_assignable  = (fn_class_assignable)GetProcAddress(rt.module, "il2cpp_class_is_assignable_from");
    }

    if (!rt.root_domain || !rt.thread_attach || !rt.assembly_open
        || !rt.assembly_image || !rt.class_from_name || !rt.class_method
        || !rt.runtime_invoke) {
        gsp_log("api: a required embedding export is missing "
                "(domain=%p attach=%p open=%p image=%p class=%p method=%p invoke=%p)",
                rt.root_domain, rt.thread_attach, rt.assembly_open,
                rt.assembly_image, rt.class_from_name, rt.class_method,
                rt.runtime_invoke);
        return FALSE;
    }
    return TRUE;
}

static BOOL try_time_class(void *assembly, const char *label)
{
    void *image, *klass;

    if (!assembly) return FALSE;
    image = rt.assembly_image(assembly);
    if (!image) return FALSE;
    klass = rt.class_from_name(image, "UnityEngine", "Time");
    if (!klass) return FALSE;

    rt.set_timeScale      = rt.class_method(klass, "set_timeScale", 1);
    rt.get_timeScale      = rt.class_method(klass, "get_timeScale", 0);
    rt.set_fixedDeltaTime = rt.class_method(klass, "set_fixedDeltaTime", 1);
    rt.get_fixedDeltaTime = rt.class_method(klass, "get_fixedDeltaTime", 0);
    if (!rt.set_timeScale) return FALSE;

    if (!label && rt.image_get_name) label = rt.image_get_name(image);
    gsp_log("found UnityEngine.Time in %s (set=%p get=%p fixed=%p)",
            label ? label : "?", rt.set_timeScale, rt.get_timeScale,
            rt.set_fixedDeltaTime);
    return TRUE;
}

#define MAX_ASSEMBLIES 2048
static void  *g_asm_list[MAX_ASSEMBLIES];
static size_t g_asm_count;

static void collect_assembly(void *assembly, void *user)
{
    (void)user;
    if (g_asm_count < MAX_ASSEMBLIES) g_asm_list[g_asm_count++] = assembly;
}

/* Recent Mono (Unity 2021+, Unity 6) resolves a name passed to
 * mono_domain_assembly_open as a file path against the working directory and
 * never looks at assemblies already loaded, so lookups by assembly name fail
 * there. Walking the loaded set works on every version. The list is copied
 * out first: mono_assembly_foreach holds the assemblies lock, and class
 * lookups take loader locks of their own. */
static void collect_assemblies(void)
{
    size_t i;

    g_asm_count = 0;
    if (rt.assembly_foreach) {
        rt.assembly_foreach(collect_assembly, NULL);
    } else if (rt.domain_get_assemblies) {
        size_t n = 0;
        void **list = rt.domain_get_assemblies(rt.domain, &n);
        for (i = 0; list && i < n && g_asm_count < MAX_ASSEMBLIES; i++)
            g_asm_list[g_asm_count++] = list[i];
    }
}

static BOOL find_time_class(void)
{
    /* UnityEngine.Time moved assemblies when Unity split its modules. */
    static const char *const assemblies[] = {
        "UnityEngine.CoreModule",   /* Unity 2017.2+ */
        "UnityEngine",              /* older, and the IL2CPP facade */
    };
    size_t i;

    for (i = 0; i < sizeof(assemblies) / sizeof(assemblies[0]); i++)
        if (try_time_class(rt.assembly_open(rt.domain, assemblies[i]), assemblies[i]))
            return TRUE;

    collect_assemblies();
    for (i = 0; i < g_asm_count; i++)
        if (try_time_class(g_asm_list[i], NULL))
            return TRUE;

    return FALSE;   /* retried by the caller, which logs once */
}

/* Looks a class up in every loaded assembly; uses the list collected last. */
static void *find_class(const char *ns, const char *name)
{
    size_t i;

    for (i = 0; i < g_asm_count; i++) {
        void *image = rt.assembly_image(g_asm_list[i]);
        void *klass = image ? rt.class_from_name(image, ns, name) : NULL;
        if (klass) return klass;
    }
    return NULL;
}

static BOOL read_static(void *klass, void *field, void *out)
{
    if (rt.mono_static_get && rt.class_vtable) {
        void *vt = rt.class_vtable(rt.domain, klass);
        if (!vt) return FALSE;
        rt.mono_static_get(vt, field, out);
        return TRUE;
    }
    if (rt.il2cpp_static_get) {
        rt.il2cpp_static_get(field, out);
        return TRUE;
    }
    return FALSE;
}

static BOOL is_instance_of(void *obj, void *klass)
{
    if (!obj || !klass) return FALSE;
    if (rt.object_isinst) return rt.object_isinst(obj, klass) != NULL;
    if (rt.object_get_class && rt.class_assignable)
        return rt.class_assignable(klass, rt.object_get_class(obj));
    return FALSE;
}

/* ------------------------------------------------------------------ */
/* Hearthstone                                                         */
/* ------------------------------------------------------------------ */

/* Hearthstone drives timeScale itself through Blizzard.T5.Core's
 * TimeScaleMgr, which writes Time.timeScale = gameTimeScale * multiplier:
 * the Battlegrounds combat ramps gameTimeScale up and the tavern sets it back
 * to 1. Writing Time.timeScale directly fights both; setting the multiplier
 * composes with them.
 *
 * For selective speed it also exposes what the client is doing, as plain
 * fields read without calling into game code from this thread:
 *   GameState.s_instance.m_powerProcessor.m_currentTaskList  non-null while a
 *     batch of server actions plays out -- a card being played, an attack,
 *     every step of a combat
 *   GameState.s_instance.m_gameEntity (TB_BaconShop).m_gamePhase  2 in combat
 * Idle animations -- the leaderboard, minions in the tavern, hovering cards --
 * run between those batches and stay at normal speed. */
static struct {
    BOOL  ready;
    void *mgr;                 /* TimeScaleMgr singleton */
    void *set_mult;            /* TimeScaleMgr.SetTimeScaleMultiplier(float) */
    void *f_mult, *f_game;     /* m_timeScaleMultiplier, m_gameTimeScale */

    void *gs_class, *f_gs_instance, *f_gs_pp, *f_gs_entity;
    void *f_pp_current;
    void *bacon_class, *f_bacon_phase;
} hs;

static BOOL hs_bind(void)
{
    void *mgr_class, *get, *pp_class, *exc = NULL;

    if (!rt.field_from_name || !rt.field_get_value) return FALSE;
    collect_assemblies();

    mgr_class = find_class("Blizzard.T5.Core.Time", "TimeScaleMgr");
    if (!mgr_class) return FALSE;
    get         = rt.class_method(mgr_class, "Get", 0);
    hs.set_mult = rt.class_method(mgr_class, "SetTimeScaleMultiplier", 1);
    hs.f_mult   = rt.field_from_name(mgr_class, "m_timeScaleMultiplier");
    hs.f_game   = rt.field_from_name(mgr_class, "m_gameTimeScale");
    if (!get || !hs.set_mult || !hs.f_mult || !hs.f_game) {
        gsp_log("hearthstone: TimeScaleMgr found but its members changed "
                "(get=%p set=%p mult=%p game=%p)", get, hs.set_mult, hs.f_mult, hs.f_game);
        return FALSE;
    }
    hs.mgr = rt.runtime_invoke(get, NULL, NULL, &exc);
    if (exc || !hs.mgr) { gsp_log("hearthstone: TimeScaleMgr.Get() failed"); return FALSE; }

    hs.gs_class    = find_class("", "GameState");
    pp_class       = find_class("", "PowerProcessor");
    hs.bacon_class = find_class("", "TB_BaconShop");
    if (hs.gs_class) {
        hs.f_gs_instance = rt.field_from_name(hs.gs_class, "s_instance");
        hs.f_gs_pp       = rt.field_from_name(hs.gs_class, "m_powerProcessor");
        hs.f_gs_entity   = rt.field_from_name(hs.gs_class, "m_gameEntity");
    }
    if (pp_class)       hs.f_pp_current  = rt.field_from_name(pp_class, "m_currentTaskList");
    if (hs.bacon_class) hs.f_bacon_phase = rt.field_from_name(hs.bacon_class, "m_gamePhase");

    gsp_log("hearthstone: TimeScaleMgr=%p; selective signals: state=%p pp=%p task=%p "
            "entity=%p phase=%p", hs.mgr, hs.f_gs_instance, hs.f_gs_pp, hs.f_pp_current,
            hs.f_gs_entity, hs.f_bacon_phase);
    hs.ready = TRUE;
    return TRUE;
}

static BOOL hs_can_select(void)
{
    return hs.ready && hs.f_gs_instance && hs.f_gs_pp && hs.f_pp_current;
}

/* TRUE while the client plays out server actions or a combat is on. */
static BOOL hs_busy(void)
{
    void *gs = NULL, *pp = NULL, *task = NULL, *entity = NULL;

    if (!read_static(hs.gs_class, hs.f_gs_instance, &gs) || !gs) return FALSE;

    rt.field_get_value(gs, hs.f_gs_pp, &pp);
    if (pp) {
        rt.field_get_value(pp, hs.f_pp_current, &task);
        if (task) return TRUE;
    }

    if (hs.f_gs_entity && hs.f_bacon_phase) {
        rt.field_get_value(gs, hs.f_gs_entity, &entity);
        if (is_instance_of(entity, hs.bacon_class)) {
            int phase = 0;
            rt.field_get_value(entity, hs.f_bacon_phase, &phase);
            if (phase == 2) return TRUE;
        }
    }
    return FALSE;
}

static float hs_read(void *field)
{
    float v = 0.0f;
    rt.field_get_value(hs.mgr, field, &v);
    return v;
}

/* Invokes a static one-float setter. Returns FALSE if the runtime threw,
 * which is how an off-main-thread rejection shows up. */
static BOOL call_set_float_on(void *method, void *obj, float value)
{
    void *args[1];
    void *exc = NULL;

    if (!method) return FALSE;
    args[0] = &value;
    rt.runtime_invoke(method, obj, args, &exc);
    if (exc) {
        gsp_log("invoke: runtime threw an exception (obj=%p)", exc);
        return FALSE;
    }
    return TRUE;
}

static BOOL call_set_float(void *method, float value)
{
    return call_set_float_on(method, NULL, value);
}

static BOOL call_get_float(void *method, float *out)
{
    void *exc = NULL;
    void *boxed;

    if (!method || !rt.object_unbox) return FALSE;
    boxed = rt.runtime_invoke(method, NULL, NULL, &exc);
    if (exc || !boxed) return FALSE;
    *out = *(float*)rt.object_unbox(boxed);
    return TRUE;
}

/* ------------------------------------------------------------------ */
/* Battlegrounds rating                                                */
/* ------------------------------------------------------------------ */

/* The rating is nowhere in the logs, but the client keeps it with the rest
 * of its account data: NetCache.Get().m_netCache is Blizzard's own Map, and
 * its valueSlots array holds one object per kind of cached data. The one of
 * class NetCache/NetCacheBaconRatingInfo carries both ratings in the backing
 * fields of its properties. Everything here is a plain read off the managed
 * heap; the only call is NetCache.Get(), once.
 *
 * The numbers go into a section of their own, Local\BgOverlayRating_<pid>,
 * so the overlay can pick them up without the two builds having to agree on
 * the layout of the control block. */

#define BACON_MAGIC 0x54524742u   /* "BGRT" */

#pragma pack(push, 1)
typedef struct {
    UINT32 magic;
    UINT32 version;
    INT32  solo;
    INT32  duo;
    UINT32 seq;       /* bumped on every change, so a reader sees movement */
} BaconShared;
#pragma pack(pop)

static BaconShared *g_bacon;
static HANDLE       g_bacon_handle;

static struct {
    BOOL  ready;
    void *netcache;            /* the NetCache singleton */
    void *f_netcache;          /* NetCache.m_netCache, a Map<Type, object> */
    void *rating_class;
    void *f_solo, *f_duo;
} bacon;

static void open_bacon_shared(void)
{
    WCHAR name[64];

    wsprintfW(name, L"Local\\BgOverlayRating_%lu", GetCurrentProcessId());
    g_bacon_handle = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE,
                                        0, sizeof(BaconShared), name);
    if (!g_bacon_handle) return;
    g_bacon = (BaconShared*)MapViewOfFile(g_bacon_handle, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(BaconShared));
    if (!g_bacon) return;
    g_bacon->magic   = BACON_MAGIC;
    g_bacon->version = 1;
}

/* A managed array's elements. MonoArray is MonoObject (vtable, sync) then
 * bounds and length, with the elements inline after them. */
static void **array_items(void *array, size_t *count)
{
    char  *p = (char*)array;
    size_t word = sizeof(void*), len;

    *count = 0;
    if (!array) return NULL;
    len = *(size_t*)(p + 3 * word);
    if (len > 4096) return NULL;      /* not an array, or not one we know */
    *count = len;
    return (void**)(p + 4 * word);
}

static BOOL bacon_bind(void)
{
    void *netcache_class, *get, *exc = NULL;

    if (bacon.ready) return TRUE;
    if (!rt.object_get_class || !rt.object_isinst) return FALSE;

    netcache_class = find_class("", "NetCache");
    bacon.rating_class = find_class("", "NetCache/NetCacheBaconRatingInfo");
    if (!bacon.rating_class) bacon.rating_class = find_class("", "NetCacheBaconRatingInfo");
    if (!netcache_class || !bacon.rating_class) return FALSE;

    get = rt.class_method(netcache_class, "Get", 0);
    bacon.f_netcache = rt.field_from_name(netcache_class, "m_netCache");
    bacon.f_solo = rt.field_from_name(bacon.rating_class, "<Rating>k__BackingField");
    bacon.f_duo  = rt.field_from_name(bacon.rating_class, "<DuosRating>k__BackingField");
    if (!get || !bacon.f_netcache || !bacon.f_solo) {
        gsp_log("rating: NetCache found but its members changed "
                "(get=%p cache=%p solo=%p duo=%p)", get, bacon.f_netcache, bacon.f_solo, bacon.f_duo);
        return FALSE;
    }

    bacon.netcache = rt.runtime_invoke(get, NULL, NULL, &exc);
    if (exc || !bacon.netcache) { gsp_log("rating: NetCache.Get() failed"); return FALSE; }

    gsp_log("rating: NetCache=%p", bacon.netcache);
    bacon.ready = TRUE;
    return TRUE;
}

static BOOL bacon_read(INT32 *solo, INT32 *duo)
{
    void  *map = NULL, *values = NULL, *map_class, *f_values;
    void **items;
    size_t n, i;

    if (!bacon.ready) return FALSE;
    rt.field_get_value(bacon.netcache, bacon.f_netcache, &map);
    if (!map) return FALSE;

    /* valueSlots sits on the map's own instantiated class. */
    map_class = rt.object_get_class(map);
    f_values  = map_class ? rt.field_from_name(map_class, "valueSlots") : NULL;
    if (!f_values) return FALSE;
    rt.field_get_value(map, f_values, &values);

    items = array_items(values, &n);
    for (i = 0; i < n; i++) {
        if (!items[i] || !is_instance_of(items[i], bacon.rating_class)) continue;
        rt.field_get_value(items[i], bacon.f_solo, solo);
        if (bacon.f_duo) rt.field_get_value(items[i], bacon.f_duo, duo);
        return TRUE;
    }
    return FALSE;   /* the cache has no rating yet: not signed in, or not fetched */
}

static void bacon_publish(void)
{
    INT32 solo = 0, duo = 0;

    if (!g_bacon || !bacon_read(&solo, &duo)) return;
    if (g_bacon->solo == solo && g_bacon->duo == duo) return;
    gsp_log("rating: solo=%d duo=%d", solo, duo);
    g_bacon->solo = solo;
    g_bacon->duo  = duo;
    g_bacon->seq++;
}

/* ------------------------------------------------------------------ */
/* Battlegrounds lobby names                                           */
/* ------------------------------------------------------------------ */

/* Power.log names the local player and their teammate, but not the other
 * opponents. GameState.m_playerInfoMap has the whole lobby. Publish the
 * player ids and their display names separately from the rating so an older
 * module without this section can still share the rating. */
#define LOBBY_NAMES_MAGIC 0x4d4e4742u   /* "BGNM" */
#define LOBBY_NAMES_MAX   16
#define LOBBY_NAME_CHARS  64

#pragma pack(push, 1)
typedef struct {
    INT32 player_id;
    WCHAR name[LOBBY_NAME_CHARS];
} LobbyNameEntry;

typedef struct {
    UINT32 magic;
    UINT32 version;
    LONG   seq;       /* odd while the entries are being replaced */
    LobbyNameEntry players[LOBBY_NAMES_MAX];
} LobbyNamesShared;
#pragma pack(pop)

static LobbyNamesShared *g_lobby_names;
static HANDLE           g_lobby_names_handle;

static struct {
    BOOL  ready;
    void *f_player_infos;
    void *info_class;
    void *f_info_name;
} lobby_names;

static void open_lobby_names_shared(void)
{
    WCHAR name[64];

    wsprintfW(name, L"Local\\BgOverlayLobbyNames_%lu", GetCurrentProcessId());
    g_lobby_names_handle = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE,
                                              0, sizeof(LobbyNamesShared), name);
    if (!g_lobby_names_handle) return;
    g_lobby_names = (LobbyNamesShared*)MapViewOfFile(g_lobby_names_handle, FILE_MAP_ALL_ACCESS,
                                                     0, 0, sizeof(LobbyNamesShared));
    if (!g_lobby_names) return;
    g_lobby_names->magic   = LOBBY_NAMES_MAGIC;
    g_lobby_names->version = 1;
}

static BOOL lobby_names_bind(void)
{
    if (lobby_names.ready) return TRUE;
    if (!hs.gs_class || !hs.f_gs_instance || !rt.object_get_class) return FALSE;

    lobby_names.f_player_infos = rt.field_from_name(hs.gs_class, "m_playerInfoMap");
    lobby_names.info_class = find_class("", "SharedPlayerInfo");
    if (lobby_names.info_class)
        lobby_names.f_info_name = rt.field_from_name(lobby_names.info_class, "m_name");
    if (!lobby_names.f_player_infos || !lobby_names.f_info_name) return FALSE;

    lobby_names.ready = TRUE;
    return TRUE;
}

static void lobby_names_publish(void)
{
    LobbyNamesShared next;
    void *gs = NULL, *map = NULL, *keys_array = NULL, *values_array = NULL;
    void *map_class, *f_keys, *f_values;
    INT32 *keys;
    void **values;
    size_t nk, nv, i;
    int count = 0;

    if (!g_lobby_names || !lobby_names.ready) return;
    if (!read_static(hs.gs_class, hs.f_gs_instance, &gs) || !gs) return;
    rt.field_get_value(gs, lobby_names.f_player_infos, &map);
    if (!map) return;

    map_class = rt.object_get_class(map);
    f_keys   = map_class ? rt.field_from_name(map_class, "keySlots") : NULL;
    f_values = map_class ? rt.field_from_name(map_class, "valueSlots") : NULL;
    if (!f_keys || !f_values) return;
    rt.field_get_value(map, f_keys, &keys_array);
    rt.field_get_value(map, f_values, &values_array);

    keys   = (INT32*)array_items(keys_array, &nk);
    values = array_items(values_array, &nv);
    if (!keys || !values) return;

    memset(&next, 0, sizeof(next));
    for (i = 0; i < nk && i < nv && count < LOBBY_NAMES_MAX; i++) {
        void *managed = NULL;
        INT32 len;
        char *chars;

        if (keys[i] <= 0 || !is_instance_of(values[i], lobby_names.info_class)) continue;
        rt.field_get_value(values[i], lobby_names.f_info_name, &managed);
        if (!managed) continue;

        /* MonoString and Il2CppString both keep length after the two-word
         * object header, followed by inline UTF-16 characters. */
        len = *(INT32*)((char*)managed + 2 * sizeof(void*));
        if (len <= 0 || len > 1024) continue;
        if (len > LOBBY_NAME_CHARS - 1) len = LOBBY_NAME_CHARS - 1;
        chars = (char*)managed + 2 * sizeof(void*) + sizeof(INT32);

        next.players[count].player_id = keys[i];
        memcpy(next.players[count].name, chars, len * sizeof(WCHAR));
        next.players[count].name[len] = 0;
        count++;
    }

    if (memcmp(g_lobby_names->players, next.players, sizeof(next.players)) == 0) return;
    InterlockedIncrement(&g_lobby_names->seq);
    memcpy(g_lobby_names->players, next.players, sizeof(next.players));
    MemoryBarrier();
    InterlockedIncrement(&g_lobby_names->seq);
    gsp_log("lobby names: %d players", count);
}

/* ------------------------------------------------------------------ */
/* Waiting for the engine                                              */
/* ------------------------------------------------------------------ */

/* Mono is loaded within the first second of a launch, long before the
 * engine is up. Attaching a thread to a half-built domain, or invoking a
 * Time icall before Unity has created its TimeManager, crashes the game in
 * UnityPlayer.dll -- which is what happened whenever the controller was
 * already running when the game started. Nothing here touches the runtime
 * beyond mono_get_root_domain, a plain global read. */

static BOOL CALLBACK find_unity_window(HWND hwnd, LPARAM lp)
{
    DWORD pid = 0;
    WCHAR cls[64];

    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != GetCurrentProcessId() || !IsWindowVisible(hwnd)) return TRUE;
    if (!GetClassNameW(hwnd, cls, 64) || lstrcmpW(cls, L"UnityWndClass") != 0) return TRUE;
    *(HWND*)lp = hwnd;
    return FALSE;
}

static ULONGLONG process_age_ms(void)
{
    FILETIME created, exited, kernel, user, now;
    ULARGE_INTEGER a, b;

    if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user))
        return 0;
    GetSystemTimeAsFileTime(&now);
    a.LowPart = created.dwLowDateTime; a.HighPart = created.dwHighDateTime;
    b.LowPart = now.dwLowDateTime;     b.HighPart = now.dwHighDateTime;
    return b.QuadPart > a.QuadPart ? (b.QuadPart - a.QuadPart) / 10000 : 0;
}

static void wait_for_engine(void)
{
    int steady = 0, ticks = 0;
    BOOL logged = FALSE;

    for (;; ticks++) {
        HWND wnd = NULL;
        DWORD_PTR res;
        BOOL ok = process_age_ms() >= 20000;

        if (ok) {
            EnumWindows(find_unity_window, (LPARAM)&wnd);
            /* The main thread pumps messages once per frame; during loading
             * it blocks and this times out. */
            ok = wnd != NULL
              && SendMessageTimeoutW(wnd, WM_NULL, 0, 0,
                                     SMTO_ABORTIFHUNG | SMTO_BLOCK, 1000, &res)
              && rt.root_domain() != NULL;
        }

        if (ok) {
            if (++steady >= 6) break;       /* 3 s in a row */
        } else {
            steady = 0;
            if (!logged) { gsp_log("waiting for the game to finish loading"); logged = TRUE; }
        }
        Sleep(500);
    }
    if (logged) gsp_log("game loaded after %d s of waiting", ticks / 2);
}

/* ------------------------------------------------------------------ */
/* Worker                                                              */
/* ------------------------------------------------------------------ */

static DWORD WINAPI worker(LPVOID unused)
{
    UINT32 last_seq = 0xFFFFFFFFu;
    double applied = -1.0;
    int attempts = 0;
    unsigned long restores = 0, switches = 0, rebind_ticks = 0, bacon_ticks = 0;
    ULONGLONG last_busy = 0;
    LONG my_gen;
    (void)unused;

    gsp_log("--- gsunity worker start ---");
    open_shared();
    open_bacon_shared();
    gsp_log("shm=%p", (void*)g_shm);

    /* The scripting runtime loads after the process starts, and injecting
     * early is normal, so wait for it instead of giving up. Logged on the
     * first miss and every tenth after, to keep the log readable. */
    while (!find_runtime()) {
        if (attempts == 0 || attempts % 10 == 9)
            gsp_log("waiting for a Unity runtime (%d s)", attempts + 1);
        if (++attempts > 60) {       /* 60 s */
            if (g_shm) {
                g_shm->backend = BACKEND_NONE;
                g_shm->state   = STATE_GAVE_UP;
            }
            gsp_log("giving up: not a Unity process, or the runtime never loaded");
            dump_modules();
            return 0;
        }
        Sleep(1000);
    }

    if (!bind_api()) {
        if (g_shm) {
            g_shm->backend = BACKEND_NONE;
            g_shm->state   = STATE_GAVE_UP;
        }
        return 0;
    }

    wait_for_engine();

    rt.domain = rt.root_domain();
    if (!rt.domain) { gsp_log("root domain is NULL"); return 0; }
    rt.thread_attach(rt.domain);
    gsp_log("attached to domain %p", rt.domain);

    /* Managed assemblies can still be loading right after startup. */
    for (attempts = 0; attempts < 60 && !find_time_class(); attempts++) {
        if (attempts == 0)
            gsp_log("waiting for UnityEngine.Time to show up");
        Sleep(1000);
    }
    if (!rt.set_timeScale) {
        gsp_log("UnityEngine.Time::set_timeScale not found (by name, nor among %d loaded "
                "assemblies; enumeration %s)", (int)g_asm_count,
                rt.assembly_foreach || rt.domain_get_assemblies ? "available" : "not exported");
        if (g_shm) {
            g_shm->backend = BACKEND_NONE;
            g_shm->state   = STATE_GAVE_UP;
        }
        return 0;
    }

    /* A second instance (a rebuilt DLL injected over a running one) finds
     * fixedDeltaTime already multiplied by the current speed; taking that as
     * the base would scale it twice. The first instance leaves the original
     * in the block for the ones after it. */
    if (g_shm && g_shm->base_fixed > 0.0f) {
        rt.base_fixed_delta = g_shm->base_fixed;
    } else {
        if (!call_get_float(rt.get_fixedDeltaTime, &rt.base_fixed_delta)
            || rt.base_fixed_delta <= 0.0f)
            rt.base_fixed_delta = 0.02f;   /* Unity's own default */
        if (g_shm) g_shm->base_fixed = rt.base_fixed_delta;
    }
    gsp_log("base fixedDeltaTime = %d/10000 s", (int)(rt.base_fixed_delta * 10000));

    /* The newest instance takes charge; older ones see the generation move
     * on and stop, so two builds never fight over timeScale. */
    my_gen = g_shm ? InterlockedIncrement(&g_shm->owner) : 0;

    hs_bind();
    bacon_bind();

    if (g_shm) {
        g_shm->backend = rt.backend;
        g_shm->state   = STATE_RUNNING;
    }

    for (;;) {
        /* Selective mode has to catch the start of a card play, which a
         * 200 ms poll would leave visibly slow. */
        Sleep(hs.ready ? 40 : 200);

        if (!g_shm) continue;
        if (g_shm->owner != my_gen) {
            gsp_log("superseded by a newer instance (gen %ld -> %ld), stopping",
                    my_gen, g_shm->owner);
            return 0;
        }

        /* TimeScaleMgr may come up after the injection, at the main menu. */
        if (!hs.ready && ++rebind_ticks % 50 == 0) hs_bind();

        /* The rating changes once a game, and only between games. */
        if (++bacon_ticks % 50 == 0) {
            if (!bacon.ready) bacon_bind();
            bacon_publish();
            if (!g_lobby_names) open_lobby_names_shared();
            if (!lobby_names.ready) lobby_names_bind();
            lobby_names_publish();
        }

        if (hs.ready) {
            double want = g_shm->mult;
            BOOL selective = (g_shm->flags & FLAG_SELECTIVE) && hs_can_select();
            BOOL fast = TRUE;
            double desired;
            float cur, ts = 0.0f, expect, diff;

            if (want < 0.01)   want = 0.01;
            if (want > 1000.0) want = 1000.0;

            if (selective) {
                ULONGLONG now = GetTickCount64();
                if (hs_busy()) last_busy = now;
                /* A combat or a chain of triggers is a run of batches with
                 * gaps of a frame or two between them; the hold keeps the
                 * speed from flickering through those gaps. */
                fast = last_busy != 0 && now - last_busy < 600;
            }
            desired = fast ? want : 1.0;

            cur = hs_read(hs.f_mult);
            /* A game-side PushTemporarySpeedIncrease raises the multiplier
             * above ours and restores it afterwards; only a lower value means
             * ours was lost. */
            if (desired != applied || cur < (float)desired - 0.0001f) {
                if (call_set_float_on(hs.set_mult, hs.mgr, (float)desired)) {
                    call_set_float(rt.set_fixedDeltaTime,
                                   rt.base_fixed_delta * (float)desired);
                    if (switches < 40 || switches % 200 == 0)
                        gsp_log("hearthstone: multiplier -> %d/100%s (#%lu)",
                                (int)(desired * 100),
                                selective ? (fast ? " [action]" : " [idle]") : "",
                                switches + 1);
                    switches++;
                    applied = desired;
                } else {
                    gsp_log("hearthstone: SetTimeScaleMultiplier threw");
                }
            }

            /* Anything else writing Time.timeScale directly (an older build
             * of this module, say) breaks the product; re-running the
             * manager's own update puts it back. */
            if (call_get_float(rt.get_timeScale, &ts)) {
                expect = hs_read(hs.f_game) * hs_read(hs.f_mult);
                diff = ts - expect;
                if (diff > 0.001f || diff < -0.001f) {
                    call_set_float_on(hs.set_mult, hs.mgr, hs_read(hs.f_mult));
                    call_get_float(rt.get_timeScale, &ts);
                }
            }
            g_shm->actual  = (double)ts;
            g_shm->patched = HS_ACTIVE | (selective ? HS_SELECTIVE : 0)
                                       | (selective && fast ? HS_FAST : 0);
            continue;
        }

        {
            double want = g_shm->mult;
            UINT32 seq  = g_shm->seq;

            if (want < 0.01)   want = 0.01;
            if (want > 1000.0) want = 1000.0;

            if (seq != last_seq || want != applied) {
                if (call_set_float(rt.set_timeScale, (float)want)) {
                    /* Physics steps are fixed-length, so at timeScale 30 the
                     * engine would run 30x as many FixedUpdates per frame and
                     * stall. Scaling the step with it keeps that count flat. */
                    call_set_float(rt.set_fixedDeltaTime,
                                   rt.base_fixed_delta * (float)want);
                    applied  = want;
                    last_seq = seq;
                    gsp_log("timeScale -> %d/100", (int)(want * 100));
                } else {
                    gsp_log("failed to set timeScale to %d/100", (int)(want * 100));
                }
            }

            {
                float now = 0.0f;
                if (call_get_float(rt.get_timeScale, &now)) {
                    g_shm->actual = (double)now;
                    /* Games drive timeScale themselves (pauses, slow-motion
                     * effects); without this the first such write would
                     * silently drop the speed-up until the next change. */
                    if (applied > 0 && now != (float)applied
                        && call_set_float(rt.set_timeScale, (float)applied)) {
                        if (restores < 20 || restores % 100 == 0)
                            gsp_log("game reset timeScale to %d/100, restored %d/100 (#%lu)",
                                    (int)(now * 100), (int)(applied * 100), restores + 1);
                        restores++;
                    }
                } else {
                    g_shm->actual = applied > 0 ? applied : 0.0;
                }
            }
        }
    }
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(inst);
        CreateThread(NULL, 0, worker, NULL, 0, NULL);
    }
    return TRUE;
}
