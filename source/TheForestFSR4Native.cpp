#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <cstdarg>
#include <mutex>
#include <atomic>
#include <string>

// ============================================================================
// NGX D3D11 ABI matching OptiScaler v0.9.5-final (dxgi.dll)
// Verified directly against dxgi.dll disassembly & MSVC x64 RTTI vtable layout
// ============================================================================

static constexpr uint32_t NGX_RESULT_SUCCESS = 0x1;
static constexpr uint32_t NGX_VERSION_API    = 0x0000015;
static constexpr int      NGX_FEATURE_DLSS   = 1;
static constexpr int      NGX_ENGINE_UNITY   = 2;

static constexpr int NGX_DLSS_FLAG_NONE           = 0;
static constexpr int NGX_DLSS_FLAG_IS_HDR         = 1 << 0; // 1
static constexpr int NGX_DLSS_FLAG_MV_LOW_RES     = 1 << 1; // 2
static constexpr int NGX_DLSS_FLAG_MV_JITTERED    = 1 << 2; // 4
static constexpr int NGX_DLSS_FLAG_DEPTH_INVERTED = 1 << 3; // 8
static constexpr int NGX_DLSS_FLAG_AUTO_EXPOSURE  = 1 << 6; // 64

struct NVSDK_NGX_Handle { unsigned int Id; };
struct NVSDK_NGX_Parameter;

// MSVC x64 reverses overloaded virtual function groups in vtable:
// [0] Set(void*) [1] Set(D3D12*) [2] Set(D3D11*) [3] Set(int) [4] Set(uint)
// [5] Set(double) [6] Set(float) [7] Set(ull)
// [8..15] Get(...) in same order, [16] Reset()
struct NgxParamVtbl {
    void     (*SetVoid)(NVSDK_NGX_Parameter* self, const char* name, void* val);
    void     (*SetD3D12)(NVSDK_NGX_Parameter* self, const char* name, void* val);
    void     (*SetD3D11)(NVSDK_NGX_Parameter* self, const char* name, ID3D11Resource* val);
    void     (*SetI)(NVSDK_NGX_Parameter* self, const char* name, int val);
    void     (*SetUI)(NVSDK_NGX_Parameter* self, const char* name, unsigned int val);
    void     (*SetD)(NVSDK_NGX_Parameter* self, const char* name, double val);
    void     (*SetF)(NVSDK_NGX_Parameter* self, const char* name, float val);
    void     (*SetULL)(NVSDK_NGX_Parameter* self, const char* name, unsigned long long val);
    uint32_t (*GetVoid)(NVSDK_NGX_Parameter* self, const char* name, void** val);
    uint32_t (*GetD3D12)(NVSDK_NGX_Parameter* self, const char* name, void** val);
    uint32_t (*GetD3D11)(NVSDK_NGX_Parameter* self, const char* name, ID3D11Resource** val);
    uint32_t (*GetI)(NVSDK_NGX_Parameter* self, const char* name, int* val);
    uint32_t (*GetUI)(NVSDK_NGX_Parameter* self, const char* name, unsigned int* val);
    uint32_t (*GetD)(NVSDK_NGX_Parameter* self, const char* name, double* val);
    uint32_t (*GetF)(NVSDK_NGX_Parameter* self, const char* name, float* val);
    uint32_t (*GetULL)(NVSDK_NGX_Parameter* self, const char* name, unsigned long long* val);
    void     (*Reset)(NVSDK_NGX_Parameter* self);
};

static inline NgxParamVtbl* Vtbl(NVSDK_NGX_Parameter* p) {
    return *reinterpret_cast<NgxParamVtbl**>(p);
}

using PFN_NGX_D3D11_Init_with_ProjectID = uint32_t (*)(
    const char* InProjectId,
    int InEngineType,
    const char* InEngineVersion,
    const wchar_t* InApplicationDataPath,
    ID3D11Device* InDevice,
    const void* InFeatureInfo,
    uint32_t InSDKVersion);

using PFN_NGX_D3D11_Init = uint32_t (*)(
    unsigned long long InApplicationId,
    const wchar_t* InApplicationDataPath,
    ID3D11Device* InDevice,
    const void* InFeatureInfo,
    uint32_t InSDKVersion);

using PFN_NGX_D3D11_GetCapabilityParameters = uint32_t (*)(NVSDK_NGX_Parameter** OutParameters);
using PFN_NGX_D3D11_AllocateParameters      = uint32_t (*)(NVSDK_NGX_Parameter** OutParameters);
using PFN_NGX_D3D11_DestroyParameters       = uint32_t (*)(NVSDK_NGX_Parameter* InParameters);
using PFN_NGX_D3D11_CreateFeature           = uint32_t (*)(ID3D11DeviceContext* InDevCtx, int InFeatureID, NVSDK_NGX_Parameter* InParameters, NVSDK_NGX_Handle** OutHandle);
using PFN_NGX_D3D11_EvaluateFeature         = uint32_t (*)(ID3D11DeviceContext* InDevCtx, const NVSDK_NGX_Handle* InFeatureHandle, const NVSDK_NGX_Parameter* InParameters, void* InCallback);
using PFN_NGX_D3D11_ReleaseFeature          = uint32_t (*)(NVSDK_NGX_Handle* InHandle);
using PFN_NGX_D3D11_Shutdown1               = uint32_t (*)(ID3D11Device* InDevice);

struct NgxApi {
    HMODULE mod = nullptr;
    PFN_NGX_D3D11_Init_with_ProjectID     InitWithProjectID     = nullptr;
    PFN_NGX_D3D11_Init                    Init                  = nullptr;
    PFN_NGX_D3D11_GetCapabilityParameters GetCapabilityParams   = nullptr;
    PFN_NGX_D3D11_AllocateParameters      AllocateParams        = nullptr;
    PFN_NGX_D3D11_DestroyParameters       DestroyParams         = nullptr;
    PFN_NGX_D3D11_CreateFeature           CreateFeature         = nullptr;
    PFN_NGX_D3D11_EvaluateFeature         EvaluateFeature       = nullptr;
    PFN_NGX_D3D11_ReleaseFeature          ReleaseFeature        = nullptr;
    PFN_NGX_D3D11_Shutdown1               Shutdown1             = nullptr;
};

struct FrameData {
    void* colorPtr  = nullptr;
    void* depthPtr  = nullptr;
    void* mvPtr     = nullptr;
    void* outputPtr = nullptr;
    int   renderW   = 0;
    int   renderH   = 0;
    int   outW      = 0;
    int   outH      = 0;
    float jitterX   = 0.0f;
    float jitterY   = 0.0f;
    float mvScaleX  = 0.0f;
    float mvScaleY  = 0.0f;
    int   quality   = 2; // MaxQuality
    float sharpness = 0.0f;
    int   reset     = 1;
    int   isHdr     = 1;
    int   depthInv  = 1;
    int   valid     = 0;
};

static std::mutex g_logMutex;
static FILE*      g_logFile = nullptr;
static std::wstring g_appDir;

static void LogMsg(const char* fmt, ...) {
    std::lock_guard<std::mutex> lock(g_logMutex);
    if (!g_logFile) return;
    SYSTEMTIME st;
    GetLocalTime(&st);
    std::fprintf(g_logFile, "[%02u:%02u:%02u.%03u] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    va_list args;
    va_start(args, fmt);
    std::vfprintf(g_logFile, fmt, args);
    va_end(args);
    std::fprintf(g_logFile, "\n");
    std::fflush(g_logFile);
}

static std::mutex g_frameMutex;
static FrameData  g_pendingFrame;

static NgxApi               g_ngx;
static ID3D11Device*        g_device        = nullptr;
static NVSDK_NGX_Parameter* g_capParams     = nullptr;
static NVSDK_NGX_Parameter* g_evalParams    = nullptr;
static NVSDK_NGX_Handle*    g_featureHandle = nullptr;
static bool                 g_ngxInitDone   = false;
static bool                 g_ngxInitFailed = false;

static int g_createdRenderW = 0;
static int g_createdRenderH = 0;
static int g_createdOutW    = 0;
static int g_createdOutH    = 0;
static int g_createdQuality = -1;
static int g_createdFlags   = -1;

static std::atomic<int> g_statusCreated{0};
static std::atomic<int> g_statusEvalCount{0};
static std::atomic<int> g_statusLastError{0};
static int g_loggedEvalErrors = 0;

static ID3D11Texture2D* ResolveTexture2D(void* nativePtr, const char* tag) {
    if (!nativePtr) return nullptr;
    IUnknown* unk = reinterpret_cast<IUnknown*>(nativePtr);
    ID3D11Texture2D* tex = nullptr;
    if (SUCCEEDED(unk->QueryInterface(IID_ID3D11Texture2D, reinterpret_cast<void**>(&tex))) && tex) {
        return tex;
    }
    ID3D11ShaderResourceView* srv = nullptr;
    if (SUCCEEDED(unk->QueryInterface(IID_ID3D11ShaderResourceView, reinterpret_cast<void**>(&srv))) && srv) {
        ID3D11Resource* res = nullptr;
        srv->GetResource(&res);
        srv->Release();
        if (res) {
            HRESULT hr = res->QueryInterface(IID_ID3D11Texture2D, reinterpret_cast<void**>(&tex));
            res->Release();
            if (SUCCEEDED(hr) && tex) return tex;
        }
    }
    LogMsg("ResolveTexture2D failed for %s (ptr=%p)", tag, nativePtr);
    return nullptr;
}

static void LogTexDesc(const char* name, ID3D11Texture2D* tex) {
    if (!tex) {
        LogMsg("  Tex[%s]: NULL", name);
        return;
    }
    D3D11_TEXTURE2D_DESC d{};
    tex->GetDesc(&d);
    LogMsg("  Tex[%s]: %ux%u fmt=%u mips=%u sample=%u bind=0x%X usage=%u misc=0x%X",
           name, d.Width, d.Height, (unsigned)d.Format, d.MipLevels,
           d.SampleDesc.Count, d.BindFlags, (unsigned)d.Usage, d.MiscFlags);
}

static bool LoadNgxApi() {
    if (g_ngx.mod) return true;

    HMODULE mod = GetModuleHandleW(L"dxgi.dll");
    if (mod) {
        wchar_t path[MAX_PATH] = {};
        GetModuleFileNameW(mod, path, MAX_PATH);
        char pathUtf8[MAX_PATH * 2] = {};
        WideCharToMultiByte(CP_UTF8, 0, path, -1, pathUtf8, sizeof(pathUtf8), nullptr, nullptr);
        LogMsg("Found loaded dxgi.dll: %s", pathUtf8);
    }

    auto bindModule = [&](HMODULE m) -> bool {
        if (!m) return false;
        g_ngx.InitWithProjectID   = reinterpret_cast<PFN_NGX_D3D11_Init_with_ProjectID>(GetProcAddress(m, "NVSDK_NGX_D3D11_Init_with_ProjectID"));
        g_ngx.Init                = reinterpret_cast<PFN_NGX_D3D11_Init>(GetProcAddress(m, "NVSDK_NGX_D3D11_Init"));
        g_ngx.GetCapabilityParams = reinterpret_cast<PFN_NGX_D3D11_GetCapabilityParameters>(GetProcAddress(m, "NVSDK_NGX_D3D11_GetCapabilityParameters"));
        g_ngx.AllocateParams      = reinterpret_cast<PFN_NGX_D3D11_AllocateParameters>(GetProcAddress(m, "NVSDK_NGX_D3D11_AllocateParameters"));
        g_ngx.DestroyParams       = reinterpret_cast<PFN_NGX_D3D11_DestroyParameters>(GetProcAddress(m, "NVSDK_NGX_D3D11_DestroyParameters"));
        g_ngx.CreateFeature       = reinterpret_cast<PFN_NGX_D3D11_CreateFeature>(GetProcAddress(m, "NVSDK_NGX_D3D11_CreateFeature"));
        g_ngx.EvaluateFeature     = reinterpret_cast<PFN_NGX_D3D11_EvaluateFeature>(GetProcAddress(m, "NVSDK_NGX_D3D11_EvaluateFeature"));
        g_ngx.ReleaseFeature      = reinterpret_cast<PFN_NGX_D3D11_ReleaseFeature>(GetProcAddress(m, "NVSDK_NGX_D3D11_ReleaseFeature"));
        g_ngx.Shutdown1           = reinterpret_cast<PFN_NGX_D3D11_Shutdown1>(GetProcAddress(m, "NVSDK_NGX_D3D11_Shutdown1"));
        if ((g_ngx.InitWithProjectID || g_ngx.Init) && g_ngx.AllocateParams && g_ngx.CreateFeature && g_ngx.EvaluateFeature && g_ngx.ReleaseFeature) {
            g_ngx.mod = m;
            return true;
        }
        return false;
    };

    if (bindModule(mod)) {
        LogMsg("Bound NGX D3D11 API directly from dxgi.dll (OptiScaler)");
        return true;
    }

    HMODULE nvngx = LoadLibraryW(L"nvngx.dll");
    if (bindModule(nvngx)) {
        LogMsg("Bound NGX D3D11 API from nvngx.dll");
        return true;
    }

    LogMsg("ERROR: Could not bind NVSDK_NGX_D3D11_* exports from dxgi.dll or nvngx.dll");
    return false;
}

static void ReleaseFeatureInternal() {
    if (g_featureHandle && g_ngx.ReleaseFeature) {
        uint32_t res = g_ngx.ReleaseFeature(g_featureHandle);
        LogMsg("ReleaseFeature -> 0x%08X", res);
        g_featureHandle = nullptr;
    }
    g_statusCreated.store(0);
    g_createdRenderW = g_createdRenderH = g_createdOutW = g_createdOutH = 0;
    g_createdQuality = -1;
    g_createdFlags   = -1;
}

static void ShutdownInternal() {
    ReleaseFeatureInternal();
    if (g_evalParams && g_ngx.DestroyParams) {
        g_ngx.DestroyParams(g_evalParams);
        g_evalParams = nullptr;
    }
    if (g_capParams && g_ngx.DestroyParams) {
        g_ngx.DestroyParams(g_capParams);
        g_capParams = nullptr;
    }
    if (g_ngxInitDone && g_device && g_ngx.Shutdown1) {
        g_ngx.Shutdown1(g_device);
    }
    if (g_device) {
        g_device->Release();
        g_device = nullptr;
    }
    g_ngxInitDone = false;
    LogMsg("Native FSR4 bridge shutdown complete");
}

static bool EnsureNgxInitialized(ID3D11Device* dev) {
    if (g_ngxInitDone) return true;
    if (g_ngxInitFailed) return false;

    if (!LoadNgxApi()) {
        g_ngxInitFailed = true;
        g_statusLastError.store(-100);
        return false;
    }

    if (g_appDir.empty()) {
        wchar_t exePath[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        std::wstring s(exePath);
        size_t slash = s.find_last_of(L"\\/");
        g_appDir = (slash != std::wstring::npos) ? s.substr(0, slash) : L".";
    }

    uint32_t res = 0xBAD00000;
    if (g_ngx.InitWithProjectID) {
        res = g_ngx.InitWithProjectID(
            "TheForestFSR4",
            NGX_ENGINE_UNITY,
            "5.6.5p4",
            g_appDir.c_str(),
            dev,
            nullptr,
            NGX_VERSION_API);
        LogMsg("NVSDK_NGX_D3D11_Init_with_ProjectID -> 0x%08X", res);
    }
    if (res != NGX_RESULT_SUCCESS && g_ngx.Init) {
        res = g_ngx.Init(0x242760ULL, g_appDir.c_str(), dev, nullptr, NGX_VERSION_API);
        LogMsg("NVSDK_NGX_D3D11_Init fallback -> 0x%08X", res);
    }
    if (res != NGX_RESULT_SUCCESS) {
        g_ngxInitFailed = true;
        g_statusLastError.store((int)res);
        return false;
    }

    if (g_ngx.GetCapabilityParams) {
        uint32_t capRes = g_ngx.GetCapabilityParams(&g_capParams);
        LogMsg("GetCapabilityParameters -> 0x%08X (ptr=%p)", capRes, (void*)g_capParams);
        if (capRes == NGX_RESULT_SUCCESS && g_capParams) {
            int avail = 0;
            uint32_t gr = Vtbl(g_capParams)->GetI(g_capParams, "SuperSampling.Available", &avail);
            LogMsg("  SuperSampling.Available: res=0x%08X val=%d", gr, avail);
        }
    }

    uint32_t allocRes = g_ngx.AllocateParams(&g_evalParams);
    LogMsg("AllocateParameters -> 0x%08X (ptr=%p)", allocRes, (void*)g_evalParams);
    if (allocRes != NGX_RESULT_SUCCESS || !g_evalParams) {
        g_ngxInitFailed = true;
        g_statusLastError.store((int)allocRes);
        return false;
    }

    g_device = dev;
    g_device->AddRef();
    g_ngxInitDone = true;
    return true;
}

static bool EnsureFeatureCreated(
    ID3D11DeviceContext* ctx,
    const FrameData& fd,
    ID3D11Texture2D* colorTex,
    ID3D11Texture2D* depthTex,
    ID3D11Texture2D* mvTex,
    ID3D11Texture2D* outTex)
{
    int flags = NGX_DLSS_FLAG_MV_LOW_RES | NGX_DLSS_FLAG_AUTO_EXPOSURE;
    if (fd.isHdr)    flags |= NGX_DLSS_FLAG_IS_HDR;
    if (fd.depthInv) flags |= NGX_DLSS_FLAG_DEPTH_INVERTED;

    if (g_featureHandle &&
        g_createdRenderW == fd.renderW &&
        g_createdRenderH == fd.renderH &&
        g_createdOutW    == fd.outW    &&
        g_createdOutH    == fd.outH    &&
        g_createdQuality == fd.quality &&
        g_createdFlags   == flags) {
        return true;
    }

    ReleaseFeatureInternal();

    LogMsg("Creating NGX SuperSampling feature: render=%dx%d -> out=%dx%d quality=%d flags=0x%X (hdr=%d depthInv=%d)",
           fd.renderW, fd.renderH, fd.outW, fd.outH, fd.quality, flags, fd.isHdr, fd.depthInv);
    LogTexDesc("Color",  colorTex);
    LogTexDesc("Depth",  depthTex);
    LogTexDesc("MV",     mvTex);
    LogTexDesc("Output", outTex);

    NgxParamVtbl* vt = Vtbl(g_evalParams);
    vt->Reset(g_evalParams);
    vt->SetUI(g_evalParams, "Width",  static_cast<unsigned int>(fd.renderW));
    vt->SetUI(g_evalParams, "Height", static_cast<unsigned int>(fd.renderH));
    vt->SetUI(g_evalParams, "OutWidth",  static_cast<unsigned int>(fd.outW));
    vt->SetUI(g_evalParams, "OutHeight", static_cast<unsigned int>(fd.outH));
    vt->SetI(g_evalParams,  "PerfQualityValue", fd.quality);
    vt->SetI(g_evalParams,  "DLSS.Feature.Create.Flags", flags);
    vt->SetI(g_evalParams,  "DLSS.Enable.Output.Subrects", 0);

    uint32_t res = g_ngx.CreateFeature(ctx, NGX_FEATURE_DLSS, g_evalParams, &g_featureHandle);
    LogMsg("NVSDK_NGX_D3D11_CreateFeature -> 0x%08X (handle=%p id=%u)",
           res, (void*)g_featureHandle, g_featureHandle ? g_featureHandle->Id : 0);

    if (res != NGX_RESULT_SUCCESS || !g_featureHandle) {
        g_statusLastError.store((int)res);
        g_statusCreated.store(0);
        return false;
    }

    g_createdRenderW = fd.renderW;
    g_createdRenderH = fd.renderH;
    g_createdOutW    = fd.outW;
    g_createdOutH    = fd.outH;
    g_createdQuality = fd.quality;
    g_createdFlags   = flags;
    g_statusCreated.store(1);
    g_statusLastError.store(NGX_RESULT_SUCCESS);
    return true;
}

static void ExecuteUpscaleOnRenderThread() {
    FrameData fd;
    {
        std::lock_guard<std::mutex> lock(g_frameMutex);
        fd = g_pendingFrame;
    }

    if (!fd.valid || fd.renderW <= 0 || fd.renderH <= 0 || fd.outW <= 0 || fd.outH <= 0) {
        return;
    }

    ID3D11Texture2D* colorTex = ResolveTexture2D(fd.colorPtr,  "Color");
    ID3D11Texture2D* depthTex = ResolveTexture2D(fd.depthPtr,  "Depth");
    ID3D11Texture2D* mvTex    = ResolveTexture2D(fd.mvPtr,     "MV");
    ID3D11Texture2D* outTex   = ResolveTexture2D(fd.outputPtr, "Output");

    if (!colorTex || !depthTex || !mvTex || !outTex) {
        if (colorTex) colorTex->Release();
        if (depthTex) depthTex->Release();
        if (mvTex)    mvTex->Release();
        if (outTex)   outTex->Release();
        g_statusLastError.store(-200);
        return;
    }

    ID3D11Device* dev = nullptr;
    colorTex->GetDevice(&dev);
    ID3D11DeviceContext* ctx = nullptr;
    if (dev) dev->GetImmediateContext(&ctx);

    if (!dev || !ctx) {
        LogMsg("ERROR: Failed to obtain ID3D11Device/ImmediateContext");
        if (ctx) ctx->Release();
        if (dev) dev->Release();
        colorTex->Release();
        depthTex->Release();
        mvTex->Release();
        outTex->Release();
        g_statusLastError.store(-201);
        return;
    }

    if (EnsureNgxInitialized(dev) && EnsureFeatureCreated(ctx, fd, colorTex, depthTex, mvTex, outTex)) {
        // Save & clear OM/PS/CS bindings to prevent D3D11 read/write hazards
        ID3D11RenderTargetView* savedRtvs[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
        ID3D11DepthStencilView* savedDsv = nullptr;
        ctx->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, savedRtvs, &savedDsv);
        ctx->OMSetRenderTargets(0, nullptr, nullptr);

        ID3D11ShaderResourceView* nullSrvs[16] = {};
        ctx->PSSetShaderResources(0, 16, nullSrvs);
        ctx->CSSetShaderResources(0, 16, nullSrvs);

        NgxParamVtbl* vt = Vtbl(g_evalParams);
        vt->SetD3D11(g_evalParams, "Color",         colorTex);
        vt->SetD3D11(g_evalParams, "Depth",         depthTex);
        vt->SetD3D11(g_evalParams, "MotionVectors", mvTex);
        vt->SetD3D11(g_evalParams, "Output",        outTex);

        vt->SetF(g_evalParams, "Jitter.Offset.X", fd.jitterX);
        vt->SetF(g_evalParams, "Jitter.Offset.Y", fd.jitterY);
        vt->SetF(g_evalParams, "MV.Scale.X",      fd.mvScaleX);
        vt->SetF(g_evalParams, "MV.Scale.Y",      fd.mvScaleY);
        vt->SetI(g_evalParams, "Reset",           fd.reset ? 1 : 0);
        vt->SetF(g_evalParams, "Sharpness",       fd.sharpness);

        vt->SetUI(g_evalParams, "DLSS.Render.Subrect.Dimensions.Width",  static_cast<unsigned int>(fd.renderW));
        vt->SetUI(g_evalParams, "DLSS.Render.Subrect.Dimensions.Height", static_cast<unsigned int>(fd.renderH));
        vt->SetUI(g_evalParams, "DLSS.Input.Color.Subrect.Base.X", 0);
        vt->SetUI(g_evalParams, "DLSS.Input.Color.Subrect.Base.Y", 0);
        vt->SetUI(g_evalParams, "DLSS.Input.Depth.Subrect.Base.X", 0);
        vt->SetUI(g_evalParams, "DLSS.Input.Depth.Subrect.Base.Y", 0);
        vt->SetUI(g_evalParams, "DLSS.Input.MV.Subrect.Base.X",    0);
        vt->SetUI(g_evalParams, "DLSS.Input.MV.Subrect.Base.Y",    0);
        vt->SetUI(g_evalParams, "DLSS.Output.Subrect.Base.X",      0);
        vt->SetUI(g_evalParams, "DLSS.Output.Subrect.Base.Y",      0);
        vt->SetF(g_evalParams,  "DLSS.Pre.Exposure",  1.0f);
        vt->SetF(g_evalParams,  "DLSS.Exposure.Scale", 1.0f);

        uint32_t evalRes = g_ngx.EvaluateFeature(ctx, g_featureHandle, g_evalParams, nullptr);
        if (evalRes == NGX_RESULT_SUCCESS) {
            int count = g_statusEvalCount.fetch_add(1) + 1;
            if (count == 1 || count == 10 || count == 60 || (count % 600) == 0) {
                LogMsg("EvaluateFeature SUCCESS count=%d (jitter=%.4f,%.4f mvScale=%.1f,%.1f reset=%d)",
                       count, fd.jitterX, fd.jitterY, fd.mvScaleX, fd.mvScaleY, fd.reset);
            }
            g_statusLastError.store(NGX_RESULT_SUCCESS);
        } else {
            g_statusLastError.store((int)evalRes);
            if (g_loggedEvalErrors < 10) {
                g_loggedEvalErrors++;
                LogMsg("ERROR: EvaluateFeature failed -> 0x%08X", evalRes);
            }
        }

        // Unbind CS UAVs/SRVs left by upscaler and restore OM render targets
        ID3D11UnorderedAccessView* nullUavs[8] = {};
        ctx->CSSetUnorderedAccessViews(0, 8, nullUavs, nullptr);
        ctx->CSSetShaderResources(0, 16, nullSrvs);
        ctx->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, savedRtvs, savedDsv);

        for (int i = 0; i < D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT; ++i) {
            if (savedRtvs[i]) savedRtvs[i]->Release();
        }
        if (savedDsv) savedDsv->Release();
    }

    ctx->Release();
    dev->Release();
    colorTex->Release();
    depthTex->Release();
    mvTex->Release();
    outTex->Release();
}

static void __stdcall OnRenderEvent(int eventId) {
    if (eventId == 1) {
        ExecuteUpscaleOnRenderThread();
    } else if (eventId == 2) {
        ReleaseFeatureInternal();
    } else if (eventId == 3) {
        ShutdownInternal();
    }
}

extern "C" {

__declspec(dllexport) void __cdecl FSR4_InitLogging(const wchar_t* logPath) {
    std::lock_guard<std::mutex> lock(g_logMutex);
    if (g_logFile) {
        std::fclose(g_logFile);
        g_logFile = nullptr;
    }
    if (logPath && logPath[0]) {
        g_logFile = _wfopen(logPath, L"w");
    }
    if (g_logFile) {
        std::fprintf(g_logFile, "=== TheForestFSR4Native initialized ===\n");
        std::fflush(g_logFile);
    }
}

__declspec(dllexport) void __cdecl FSR4_SetFrameData(
    void* colorPtr,
    void* depthPtr,
    void* mvPtr,
    void* outputPtr,
    int   renderW,
    int   renderH,
    int   outW,
    int   outH,
    float jitterX,
    float jitterY,
    float mvScaleX,
    float mvScaleY,
    int   qualityValue,
    float sharpness,
    int   reset,
    int   isHdr,
    int   depthInverted)
{
    std::lock_guard<std::mutex> lock(g_frameMutex);
    g_pendingFrame.colorPtr  = colorPtr;
    g_pendingFrame.depthPtr  = depthPtr;
    g_pendingFrame.mvPtr     = mvPtr;
    g_pendingFrame.outputPtr = outputPtr;
    g_pendingFrame.renderW   = renderW;
    g_pendingFrame.renderH   = renderH;
    g_pendingFrame.outW      = outW;
    g_pendingFrame.outH      = outH;
    g_pendingFrame.jitterX   = jitterX;
    g_pendingFrame.jitterY   = jitterY;
    g_pendingFrame.mvScaleX  = mvScaleX;
    g_pendingFrame.mvScaleY  = mvScaleY;
    g_pendingFrame.quality   = qualityValue;
    g_pendingFrame.sharpness = sharpness;
    g_pendingFrame.reset     = reset;
    g_pendingFrame.isHdr     = isHdr;
    g_pendingFrame.depthInv  = depthInverted;
    g_pendingFrame.valid     = 1;
}

__declspec(dllexport) void* __cdecl FSR4_GetRenderEventFunc() {
    return reinterpret_cast<void*>(&OnRenderEvent);
}

__declspec(dllexport) void __cdecl FSR4_GetStatus(int* outCreated, int* outEvalCount, int* outLastError) {
    if (outCreated)   *outCreated   = g_statusCreated.load();
    if (outEvalCount) *outEvalCount = g_statusEvalCount.load();
    if (outLastError) *outLastError = g_statusLastError.load();
}

} // extern "C"

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_DETACH) {
        std::lock_guard<std::mutex> lock(g_logMutex);
        if (g_logFile) {
            std::fprintf(g_logFile, "=== TheForestFSR4Native DLL_PROCESS_DETACH ===\n");
            std::fclose(g_logFile);
            g_logFile = nullptr;
        }
    }
    return TRUE;
}
