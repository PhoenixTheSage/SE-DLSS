#include "SeDlssNgx.h"
#include "ngx_min.h"

#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>

#include "Shaders/bytecode/FullscreenVs.h"
#include "Shaders/bytecode/MvPs.h"
#include "Shaders/bytecode/DepthUpsamplePs.h"

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

namespace
{
std::mutex g_mutex;
std::string g_lastError = "not initialized";
HMODULE g_ngx;
ID3D11Device* g_device;
NVSDK_NGX_Parameter* g_capabilityParams;
NVSDK_NGX_Parameter* g_evalParams;
NVSDK_NGX_Handle* g_dlss;
bool g_initialized;
bool g_supported;
int g_quality = -1;
int g_preset = -1;
uint32_t g_outW;
uint32_t g_outH;
std::wstring g_searchPath;
std::vector<std::wstring> g_ngxPaths;
std::vector<const wchar_t*> g_ngxPathPtrs;

ID3D11Texture2D* g_mvTex;
ID3D11RenderTargetView* g_mvRtv;
ID3D11ShaderResourceView* g_mvSrv;
ID3D11VertexShader* g_mvVs;
ID3D11PixelShader* g_mvPs;
ID3D11Buffer* g_mvCb;
ID3D11SamplerState* g_mvSampler;
uint32_t g_mvW;
uint32_t g_mvH;
ID3D11Resource* g_cachedDepthRes;
ID3D11ShaderResourceView* g_cachedDepthSrv;
ID3D11Resource* g_cachedUpSrcRes;
ID3D11ShaderResourceView* g_cachedUpSrcSrv;
ID3D11Resource* g_cachedUpDestRes;
ID3D11DepthStencilView* g_cachedUpDestDsv;
int g_createFlags = -1;

ID3D11VertexShader* g_depthVs;
ID3D11PixelShader* g_depthPs;
ID3D11SamplerState* g_depthSampler;
ID3D11DepthStencilState* g_depthWriteAlways;

ID3D11Texture2D* g_evalOutTex;
uint32_t g_evalOutW;
uint32_t g_evalOutH;
DXGI_FORMAT g_evalOutFmt = DXGI_FORMAT_UNKNOWN;
ID3D11Resource* g_cachedEvalDest;
D3D11_TEXTURE2D_DESC g_cachedEvalDestDesc{};
bool g_cachedEvalDestHasUav;
FILE* g_debugLog;
char g_lastEvalLog[256];

using PFN_InitProject = NVSDK_NGX_Result(NVSDK_CONV*)(const char*, NVSDK_NGX_EngineType, const char*, const wchar_t*, ID3D11Device*, NVSDK_NGX_Version, const NVSDK_NGX_FeatureCommonInfo*);
using PFN_InitProjectSdk = NVSDK_NGX_Result(NVSDK_CONV*)(const char*, NVSDK_NGX_EngineType, const char*, const wchar_t*, ID3D11Device*, const NVSDK_NGX_FeatureCommonInfo*, NVSDK_NGX_Version);
using PFN_InitApp = NVSDK_NGX_Result(NVSDK_CONV*)(unsigned long long, const wchar_t*, ID3D11Device*, const NVSDK_NGX_FeatureCommonInfo*, NVSDK_NGX_Version);
using PFN_Shutdown1 = NVSDK_NGX_Result(NVSDK_CONV*)(ID3D11Device*);
using PFN_GetCaps = NVSDK_NGX_Result(NVSDK_CONV*)(NVSDK_NGX_Parameter**);
using PFN_AllocParams = NVSDK_NGX_Result(NVSDK_CONV*)(NVSDK_NGX_Parameter**);
using PFN_DestroyParams = NVSDK_NGX_Result(NVSDK_CONV*)(NVSDK_NGX_Parameter*);
using PFN_CreateFeature = NVSDK_NGX_Result(NVSDK_CONV*)(ID3D11DeviceContext*, NVSDK_NGX_Feature, NVSDK_NGX_Parameter*, NVSDK_NGX_Handle**);
using PFN_ReleaseFeature = NVSDK_NGX_Result(NVSDK_CONV*)(NVSDK_NGX_Handle*);
using PFN_Evaluate = NVSDK_NGX_Result(NVSDK_CONV*)(ID3D11DeviceContext*, const NVSDK_NGX_Handle*, const NVSDK_NGX_Parameter*, void*);

PFN_InitProject pInitProject;
PFN_InitProjectSdk pInitProjectSdk;
PFN_InitApp pInitApp;
PFN_Shutdown1 pShutdown1;
PFN_GetCaps pGetCaps;
PFN_AllocParams pAllocParams;
PFN_DestroyParams pDestroyParams;
PFN_CreateFeature pCreateFeature;
PFN_ReleaseFeature pReleaseFeature;
PFN_Evaluate pEvaluate;

void DebugLogLine(const char* fmt, ...)
{
    if (!g_debugLog)
        return;
    SYSTEMTIME st{};
    GetLocalTime(&st);
    fprintf(g_debugLog, "%02u:%02u:%02u.%03u ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    va_list ap;
    va_start(ap, fmt);
    vfprintf(g_debugLog, fmt, ap);
    va_end(ap);
    fputc('\n', g_debugLog);
    fflush(g_debugLog);
}

void OpenDebugLog(const wchar_t* path)
{
    if (g_debugLog || !path || !path[0])
        return;
    if (_wfopen_s(&g_debugLog, path, L"w") != 0 || !g_debugLog)
    {
        g_debugLog = nullptr;
        return;
    }
    DebugLogLine("SeDlssNgx debug log opened");
}

void CloseDebugLog()
{
    if (!g_debugLog)
        return;
    DebugLogLine("SeDlssNgx debug log closed");
    fclose(g_debugLog);
    g_debugLog = nullptr;
    g_lastEvalLog[0] = 0;
}

void SetError(const char* text)
{
    g_lastError = text ? text : "unknown error";
}

template<typename T>
T LoadFn(const char* name)
{
    auto fn = reinterpret_cast<T>(GetProcAddress(g_ngx, name));
    return fn;
}

template<typename T>
T LoadFn2(const char* primary, const char* secondary)
{
    if (auto fn = LoadFn<T>(primary))
        return fn;
    return LoadFn<T>(secondary);
}

void ReleaseMvRt(ID3D11Texture2D*& tex, ID3D11RenderTargetView*& rtv, ID3D11ShaderResourceView*& srv)
{
    if (rtv) { rtv->Release(); rtv = nullptr; }
    if (srv) { srv->Release(); srv = nullptr; }
    if (tex) { tex->Release(); tex = nullptr; }
}

void ReleaseCachedViews()
{
    if (g_cachedDepthSrv) { g_cachedDepthSrv->Release(); g_cachedDepthSrv = nullptr; }
    g_cachedDepthRes = nullptr;
    if (g_cachedUpSrcSrv) { g_cachedUpSrcSrv->Release(); g_cachedUpSrcSrv = nullptr; }
    g_cachedUpSrcRes = nullptr;
    if (g_cachedUpDestDsv) { g_cachedUpDestDsv->Release(); g_cachedUpDestDsv = nullptr; }
    g_cachedUpDestRes = nullptr;
}

void ReleaseMvPipeline()
{
    ReleaseMvRt(g_mvTex, g_mvRtv, g_mvSrv);
    g_mvW = g_mvH = 0;
}

void ReleaseEvalOutput()
{
    if (g_evalOutTex) { g_evalOutTex->Release(); g_evalOutTex = nullptr; }
    g_evalOutW = g_evalOutH = 0;
    g_evalOutFmt = DXGI_FORMAT_UNKNOWN;
    g_cachedEvalDest = nullptr;
    g_cachedEvalDestHasUav = false;
}

DXGI_FORMAT DepthSrvFormat(DXGI_FORMAT resourceFormat)
{
    switch (resourceFormat)
    {
    case DXGI_FORMAT_R32G8X24_TYPELESS:
    case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
        return DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
    case DXGI_FORMAT_R24G8_TYPELESS:
    case DXGI_FORMAT_D24_UNORM_S8_UINT:
        return DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
    case DXGI_FORMAT_R32_TYPELESS:
    case DXGI_FORMAT_D32_FLOAT:
        return DXGI_FORMAT_R32_FLOAT;
    case DXGI_FORMAT_R16_TYPELESS:
    case DXGI_FORMAT_D16_UNORM:
        return DXGI_FORMAT_R16_UNORM;
    default:
        return resourceFormat;
    }
}

DXGI_FORMAT DepthDsvFormat(DXGI_FORMAT resourceFormat)
{
    switch (resourceFormat)
    {
    case DXGI_FORMAT_R32G8X24_TYPELESS:
    case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
        return DXGI_FORMAT_D32_FLOAT_S8X24_UINT;
    case DXGI_FORMAT_R24G8_TYPELESS:
    case DXGI_FORMAT_D24_UNORM_S8_UINT:
        return DXGI_FORMAT_D24_UNORM_S8_UINT;
    case DXGI_FORMAT_R32_TYPELESS:
    case DXGI_FORMAT_D32_FLOAT:
        return DXGI_FORMAT_D32_FLOAT;
    case DXGI_FORMAT_R16_TYPELESS:
    case DXGI_FORMAT_D16_UNORM:
        return DXGI_FORMAT_D16_UNORM;
    default:
        return resourceFormat;
    }
}

void ReleaseDepthUpsample()
{
    if (g_depthVs) { g_depthVs->Release(); g_depthVs = nullptr; }
    if (g_depthPs) { g_depthPs->Release(); g_depthPs = nullptr; }
    if (g_depthSampler) { g_depthSampler->Release(); g_depthSampler = nullptr; }
    if (g_depthWriteAlways) { g_depthWriteAlways->Release(); g_depthWriteAlways = nullptr; }
}

bool EnsureDepthUpsample(ID3D11Device* device)
{
    if (g_depthVs && g_depthPs && g_depthSampler && g_depthWriteAlways)
        return true;

    HRESULT hr = device->CreateVertexShader(kFullscreenVsBytecode, sizeof(kFullscreenVsBytecode), nullptr, &g_depthVs);
    if (FAILED(hr))
    {
        SetError("failed to create depth-upsample VS");
        return false;
    }
    hr = device->CreatePixelShader(kDepthUpsamplePsBytecode, sizeof(kDepthUpsamplePsBytecode), nullptr, &g_depthPs);
    if (FAILED(hr))
    {
        SetError("failed to create depth-upsample PS");
        return false;
    }

    D3D11_SAMPLER_DESC samp{};
    samp.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
    samp.AddressU = samp.AddressV = samp.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    hr = device->CreateSamplerState(&samp, &g_depthSampler);
    if (FAILED(hr))
    {
        SetError("failed to create depth-upsample sampler");
        return false;
    }

    D3D11_DEPTH_STENCIL_DESC ds{};
    ds.DepthEnable = TRUE;
    ds.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
    ds.DepthFunc = D3D11_COMPARISON_ALWAYS;
    hr = device->CreateDepthStencilState(&ds, &g_depthWriteAlways);
    if (FAILED(hr))
    {
        SetError("failed to create depth-upsample DSS");
        return false;
    }
    return true;
}

bool QueryTexture2D(ID3D11Resource* res, D3D11_TEXTURE2D_DESC* desc)
{
    if (!res || !desc)
        return false;
    ID3D11Texture2D* tex = nullptr;
    if (FAILED(res->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&tex))) || !tex)
        return false;
    tex->GetDesc(desc);
    tex->Release();
    return true;
}

bool DescribeEvalDest(ID3D11Resource* output, D3D11_TEXTURE2D_DESC* desc, bool* hasUav)
{
    if (g_cachedEvalDest == output)
    {
        *desc = g_cachedEvalDestDesc;
        *hasUav = g_cachedEvalDestHasUav;
        return true;
    }
    if (!QueryTexture2D(output, desc))
        return false;
    g_cachedEvalDest = output;
    g_cachedEvalDestDesc = *desc;
    g_cachedEvalDestHasUav = (desc->BindFlags & D3D11_BIND_UNORDERED_ACCESS) != 0;
    *hasUav = g_cachedEvalDestHasUav;
    return true;
}

bool EnsureEvalOutput(ID3D11Device* device, const D3D11_TEXTURE2D_DESC& destDesc)
{
    if (g_evalOutTex && g_evalOutW == destDesc.Width && g_evalOutH == destDesc.Height && g_evalOutFmt == destDesc.Format)
        return true;
    ReleaseEvalOutput();

    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = destDesc.Width;
    desc.Height = destDesc.Height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = destDesc.Format;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    HRESULT hr = device->CreateTexture2D(&desc, nullptr, &g_evalOutTex);
    if (FAILED(hr))
    {
        char buf[160];
        sprintf_s(buf, "failed to create UAV evaluate target %ux%u fmt=%u (hr=0x%08X)",
            destDesc.Width, destDesc.Height, (unsigned)destDesc.Format, (unsigned)hr);
        SetError(buf);
        return false;
    }
    g_evalOutW = desc.Width;
    g_evalOutH = desc.Height;
    g_evalOutFmt = desc.Format;
    return true;
}

const char* NgxResultName(NVSDK_NGX_Result result)
{
    switch ((uint32_t)result & 0xFFFFu)
    {
    case 1: return "FeatureNotSupported";
    case 2: return "PlatformError";
    case 3: return "FeatureAlreadyExists";
    case 4: return "FeatureNotFound";
    case 5: return "InvalidParameter";
    case 6: return "ScratchBufferTooSmall";
    case 7: return "NotInitialized";
    case 8: return "UnsupportedInputFormat";
    case 9: return "RWFlagMissing";
    case 10: return "MissingInput";
    case 11: return "UnableToInitializeFeature";
    case 12: return "OutOfDate";
    case 13: return "OutOfGPUMemory";
    case 14: return "UnsupportedFormat";
    default: return "Fail";
    }
}

void ReleaseMvShaders()
{
    if (g_mvVs) { g_mvVs->Release(); g_mvVs = nullptr; }
    if (g_mvPs) { g_mvPs->Release(); g_mvPs = nullptr; }
    if (g_mvCb) { g_mvCb->Release(); g_mvCb = nullptr; }
    if (g_mvSampler) { g_mvSampler->Release(); g_mvSampler = nullptr; }
}

bool EnsureDepthSrv(ID3D11Device* device, ID3D11Resource* res, const D3D11_TEXTURE2D_DESC& desc,
    ID3D11Resource*& cachedRes, ID3D11ShaderResourceView*& cachedSrv)
{
    if (cachedSrv && cachedRes == res)
        return true;
    if (cachedSrv)
    {
        cachedSrv->Release();
        cachedSrv = nullptr;
    }
    cachedRes = res;

    DXGI_FORMAT srvFormats[] = {
        DepthSrvFormat(desc.Format),
        DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS,
        DXGI_FORMAT_R32_FLOAT,
        DXGI_FORMAT_R24_UNORM_X8_TYPELESS
    };
    D3D11_SHADER_RESOURCE_VIEW_DESC srvDesc{};
    srvDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvDesc.Texture2D.MipLevels = 1;
    for (DXGI_FORMAT fmt : srvFormats)
    {
        if (fmt == DXGI_FORMAT_UNKNOWN)
            continue;
        srvDesc.Format = fmt;
        if (SUCCEEDED(device->CreateShaderResourceView(res, &srvDesc, &cachedSrv)))
            return true;
        cachedSrv = nullptr;
    }
    return false;
}

bool EnsureMvShaders(ID3D11Device* device)
{
    if (g_mvVs && g_mvPs && g_mvCb && g_mvSampler)
        return true;

    HRESULT hr = device->CreateVertexShader(kFullscreenVsBytecode, sizeof(kFullscreenVsBytecode), nullptr, &g_mvVs);
    if (FAILED(hr))
    {
        SetError("failed to create motion-vector VS");
        return false;
    }
    hr = device->CreatePixelShader(kMvPsBytecode, sizeof(kMvPsBytecode), nullptr, &g_mvPs);
    if (FAILED(hr))
    {
        SetError("failed to create motion-vector PS");
        return false;
    }

    D3D11_BUFFER_DESC cb{};
    cb.ByteWidth = 208;
    cb.Usage = D3D11_USAGE_DYNAMIC;
    cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    cb.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    hr = device->CreateBuffer(&cb, nullptr, &g_mvCb);
    if (FAILED(hr))
    {
        SetError("failed to create motion-vector constant buffer");
        return false;
    }

    D3D11_SAMPLER_DESC samp{};
    samp.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
    samp.AddressU = samp.AddressV = samp.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    hr = device->CreateSamplerState(&samp, &g_mvSampler);
    if (FAILED(hr))
    {
        SetError("failed to create motion-vector sampler");
        return false;
    }
    return true;
}

bool CreateMvRt(ID3D11Device* device, uint32_t width, uint32_t height,
    ID3D11Texture2D** tex, ID3D11RenderTargetView** rtv, ID3D11ShaderResourceView** srv)
{
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R16G16_FLOAT;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    if (FAILED(device->CreateTexture2D(&desc, nullptr, tex)))
        return false;
    if (FAILED(device->CreateRenderTargetView(*tex, nullptr, rtv)))
        return false;
    return SUCCEEDED(device->CreateShaderResourceView(*tex, nullptr, srv));
}

bool EnsureMvTarget(ID3D11Device* device, uint32_t width, uint32_t height)
{
    if (g_mvTex && g_mvW == width && g_mvH == height)
        return true;
    ReleaseMvPipeline();
    if (!CreateMvRt(device, width, height, &g_mvTex, &g_mvRtv, &g_mvSrv))
    {
        ReleaseMvPipeline();
        SetError("failed to create motion-vector targets");
        return false;
    }
    g_mvW = width;
    g_mvH = height;
    return true;
}

void DescribeTexture(ID3D11Resource* res, char* buf, size_t n)
{
    if (!res)
    {
        strcpy_s(buf, n, "null");
        return;
    }
    ID3D11Texture2D* tex = nullptr;
    if (FAILED(res->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&tex))) || !tex)
    {
        strcpy_s(buf, n, "not-tex2d");
        return;
    }
    D3D11_TEXTURE2D_DESC desc{};
    tex->GetDesc(&desc);
    tex->Release();
    sprintf_s(buf, n, "%ux%u fmt=%u bind=0x%x", desc.Width, desc.Height, (unsigned)desc.Format, desc.BindFlags);
}

void UnbindPipeline(ID3D11DeviceContext* ctx)
{
    ID3D11RenderTargetView* rtvs[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT]{};
    ctx->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtvs, nullptr);
    ID3D11ShaderResourceView* srvs[8]{};
    ctx->PSSetShaderResources(0, 8, srvs);
    ctx->VSSetShaderResources(0, 8, srvs);
    ctx->CSSetShaderResources(0, 8, srvs);
    ID3D11UnorderedAccessView* uavs[8]{};
    UINT initial[8];
    for (int i = 0; i < 8; ++i)
        initial[i] = 0xFFFFFFFFu;
    ctx->CSSetUnorderedAccessViews(0, 8, uavs, initial);
}

void ReleaseDlss()
{
    if (g_dlss && pReleaseFeature)
    {
        pReleaseFeature(g_dlss);
        g_dlss = nullptr;
    }
    g_quality = -1;
    g_preset = -1;
    g_createFlags = -1;
    g_outW = g_outH = 0;
}

std::wstring JoinPath(const std::wstring& dir, const wchar_t* file)
{
    if (dir.empty())
        return file ? file : L"";
    if (dir.back() == L'\\' || dir.back() == L'/')
        return dir + file;
    return dir + L'\\' + file;
}

std::string Narrow(const std::wstring& wide)
{
    if (wide.empty())
        return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), (int)wide.size(), nullptr, 0, nullptr, nullptr);
    if (n <= 0)
        return {};
    std::string out((size_t)n, 0);
    WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), (int)wide.size(), &out[0], n, nullptr, nullptr);
    return out;
}

std::wstring ReadNgxCoreDir()
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\NVIDIA Corporation\\Global\\NGXCore", 0,
            KEY_READ | KEY_WOW64_64KEY, &key) != ERROR_SUCCESS)
        return {};
    wchar_t buf[MAX_PATH]{};
    DWORD size = sizeof(buf);
    DWORD type = 0;
    LONG st = RegQueryValueExW(key, L"FullPath", nullptr, &type, reinterpret_cast<LPBYTE>(buf), &size);
    RegCloseKey(key);
    if (st != ERROR_SUCCESS || (type != REG_SZ && type != REG_EXPAND_SZ) || buf[0] == 0)
        return {};
    return buf;
}

void AddDriverStoreCandidates(std::vector<std::wstring>& candidates)
{
    wchar_t sys[MAX_PATH]{};
    if (!GetSystemDirectoryW(sys, MAX_PATH))
        return;
    std::wstring repo = JoinPath(sys, L"DriverStore\\FileRepository");
    WIN32_FIND_DATAW fd{};
    HANDLE find = FindFirstFileW(JoinPath(repo, L"nv*").c_str(), &fd);
    if (find == INVALID_HANDLE_VALUE)
        return;
    do
    {
        if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
            continue;
        candidates.push_back(JoinPath(JoinPath(repo, fd.cFileName), L"_nvngx.dll"));
    } while (FindNextFileW(find, &fd));
    FindClose(find);
}

HMODULE TryLoadNgx(const std::wstring& path)
{
    if (path.empty())
        return nullptr;
    DWORD attrib = GetFileAttributesW(path.c_str());
    if (attrib == INVALID_FILE_ATTRIBUTES)
        return nullptr;
    HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!module)
        module = LoadLibraryW(path.c_str());
    return module;
}

HMODULE LoadNgxModule(std::wstring& loadedFrom, DWORD& lastError)
{
    lastError = 0;
    loadedFrom.clear();
    std::vector<std::wstring> candidates;

    wchar_t sys[MAX_PATH]{};
    if (GetSystemDirectoryW(sys, MAX_PATH))
    {
        candidates.push_back(JoinPath(sys, L"_nvngx.dll"));
        candidates.push_back(JoinPath(sys, L"nvngx.dll"));
    }

    std::wstring ngxCore = ReadNgxCoreDir();
    if (!ngxCore.empty())
    {
        candidates.push_back(JoinPath(ngxCore, L"_nvngx.dll"));
        candidates.push_back(JoinPath(ngxCore, L"nvngx.dll"));
    }

    AddDriverStoreCandidates(candidates);
    candidates.push_back(L"_nvngx.dll");
    candidates.push_back(L"nvngx.dll");

    DWORD err = 0;
    for (const auto& path : candidates)
    {
        HMODULE module = TryLoadNgx(path);
        if (module)
        {
            loadedFrom = path;
            return module;
        }
        err = GetLastError();
        if (err != 0)
            lastError = err;
    }
    return nullptr;
}

void BuildFeatureSearchPaths(const wchar_t* pluginPath)
{
    g_ngxPaths.clear();
    g_ngxPathPtrs.clear();
    auto addUnique = [](const std::wstring& path)
    {
        if (path.empty())
            return;
        for (const auto& existing : g_ngxPaths)
        {
            if (_wcsicmp(existing.c_str(), path.c_str()) == 0)
                return;
        }
        g_ngxPaths.push_back(path);
    };

    if (pluginPath && pluginPath[0])
        addUnique(pluginPath);
    addUnique(ReadNgxCoreDir());
    for (auto& path : g_ngxPaths)
        g_ngxPathPtrs.push_back(path.c_str());
}

bool IsForcedRenderPreset(int preset)
{
    return (preset >= (int)NVSDK_NGX_DLSS_Hint_Render_Preset_A &&
            preset <= (int)NVSDK_NGX_DLSS_Hint_Render_Preset_F) ||
           (preset >= (int)NVSDK_NGX_DLSS_Hint_Render_Preset_J &&
            preset <= (int)NVSDK_NGX_DLSS_Hint_Render_Preset_M);
}

void ApplyHintPresets(NVSDK_NGX_Parameter* params, int preset)
{
    unsigned int k = (unsigned int)NVSDK_NGX_DLSS_Hint_Render_Preset_K;
    unsigned int dlaa = k, quality = k, balanced = k, perf = k, ultra = k, ultraQ = k;
    if (IsForcedRenderPreset(preset))
    {
        unsigned int forced = (unsigned int)preset;
        dlaa = quality = balanced = perf = ultra = ultraQ = forced;
    }
    params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA, dlaa);
    params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality, quality);
    params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Balanced, balanced);
    params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Performance, perf);
    params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraPerformance, ultra);
    params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraQuality, ultraQ);
}
}

const unsigned kVendorNvidia = 0x10DE;

const char* VendorName(unsigned vendorId)
{
    switch (vendorId)
    {
    case 0x10DE: return "NVIDIA";
    case 0x1002: return "AMD";
    case 0x8086: return "Intel";
    case 0x1414: return "Microsoft";
    default: return "unknown vendor";
    }
}

bool TryReadAdapter(ID3D11Device* device, unsigned& vendorId, std::string& adapterName)
{
    vendorId = 0;
    adapterName.clear();
    if (!device)
        return false;

    IDXGIDevice* dxgiDevice = nullptr;
    if (FAILED(device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDevice))) || !dxgiDevice)
        return false;

    IDXGIAdapter* adapter = nullptr;
    HRESULT hr = dxgiDevice->GetAdapter(&adapter);
    dxgiDevice->Release();
    if (FAILED(hr) || !adapter)
        return false;

    DXGI_ADAPTER_DESC desc{};
    hr = adapter->GetDesc(&desc);
    adapter->Release();
    if (FAILED(hr))
        return false;

    vendorId = desc.VendorId;
    adapterName = Narrow(std::wstring(desc.Description));
    return true;
}

extern "C" int SeDlss_Init(const SeDlssInitArgs* args)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_initialized)
        return 1;
    if (!args || !args->Device)
    {
        SetError("Init requires a D3D11 device");
        return 0;
    }

    auto* device = static_cast<ID3D11Device*>(args->Device);
    if (args->DllSearchPath && args->DllSearchPath[0])
        g_searchPath = args->DllSearchPath;

    OpenDebugLog(args->DebugLogPath);
#ifdef _DEBUG
    if (!g_debugLog && args->LogPath && args->LogPath[0])
    {
        std::wstring fallback = JoinPath(args->LogPath, L"SeDlssNgx.debug.log");
        OpenDebugLog(fallback.c_str());
    }
#endif
    DebugLogLine("Init search=%s", Narrow(g_searchPath).c_str());

    unsigned vendorId = 0;
    std::string adapterName;
    if (TryReadAdapter(device, vendorId, adapterName))
    {
        DebugLogLine("GPU %s (%s) vendor=0x%04X",
            VendorName(vendorId),
            adapterName.empty() ? "unknown adapter" : adapterName.c_str(),
            vendorId);
        if (vendorId != kVendorNvidia)
        {
            char buf[384];
            sprintf_s(buf, "DLSS requires an NVIDIA GPU. Detected %s (%s)",
                VendorName(vendorId), adapterName.empty() ? "unknown adapter" : adapterName.c_str());
            SetError(buf);
            DebugLogLine("%s", buf);
            return 0;
        }
    }

    // Current NVIDIA drivers keep _nvngx.dll in the DriverStore, not System32.
    // Load it by absolute path so a process-wide SetDllDirectory cannot hide it.
    std::wstring loadedFrom;
    DWORD loadError = 0;
    g_ngx = LoadNgxModule(loadedFrom, loadError);
    if (!g_ngx)
    {
        std::string ngxCore = Narrow(ReadNgxCoreDir());
        char buf[512];
        sprintf_s(buf, "failed to load _nvngx.dll (Win32 %lu). NGXCore=%s",
            loadError, ngxCore.empty() ? "(registry missing)" : ngxCore.c_str());
        SetError(buf);
        DebugLogLine("%s", buf);
        return 0;
    }
    DebugLogLine("loaded NGX from %s", Narrow(loadedFrom).c_str());

    // Driver _nvngx.dll exports Init_ProjectID / EvaluateFeature.
    // The NGX SDK static lib uses Init_with_ProjectID / EvaluateFeature_C.
    pInitProject = LoadFn<PFN_InitProject>("NVSDK_NGX_D3D11_Init_ProjectID");
    pInitProjectSdk = LoadFn<PFN_InitProjectSdk>("NVSDK_NGX_D3D11_Init_with_ProjectID");
    pInitApp = LoadFn<PFN_InitApp>("NVSDK_NGX_D3D11_Init_Ext");
    if (!pInitApp)
        pInitApp = LoadFn<PFN_InitApp>("NVSDK_NGX_D3D11_Init");
    pShutdown1 = LoadFn<PFN_Shutdown1>("NVSDK_NGX_D3D11_Shutdown1");
    pGetCaps = LoadFn<PFN_GetCaps>("NVSDK_NGX_D3D11_GetCapabilityParameters");
    pAllocParams = LoadFn<PFN_AllocParams>("NVSDK_NGX_D3D11_AllocateParameters");
    pDestroyParams = LoadFn<PFN_DestroyParams>("NVSDK_NGX_D3D11_DestroyParameters");
    pCreateFeature = LoadFn<PFN_CreateFeature>("NVSDK_NGX_D3D11_CreateFeature");
    pReleaseFeature = LoadFn<PFN_ReleaseFeature>("NVSDK_NGX_D3D11_ReleaseFeature");
    pEvaluate = LoadFn2<PFN_Evaluate>("NVSDK_NGX_D3D11_EvaluateFeature", "NVSDK_NGX_D3D11_EvaluateFeature_C");
    if ((!pInitProject && !pInitProjectSdk && !pInitApp) || !pShutdown1 || !pGetCaps || !pCreateFeature || !pReleaseFeature || !pEvaluate)
    {
        char buf[384];
        sprintf_s(buf, "NGX driver exports are missing (init=%d/%d/%d shut=%d caps=%d create=%d release=%d eval=%d) from %s",
            pInitProject ? 1 : 0, pInitProjectSdk ? 1 : 0, pInitApp ? 1 : 0,
            pShutdown1 ? 1 : 0, pGetCaps ? 1 : 0, pCreateFeature ? 1 : 0,
            pReleaseFeature ? 1 : 0, pEvaluate ? 1 : 0,
            Narrow(loadedFrom).c_str());
        SetError(buf);
        DebugLogLine("%s", buf);
        return 0;
    }

    NVSDK_NGX_FeatureCommonInfo info{};
    BuildFeatureSearchPaths(g_searchPath.empty() ? nullptr : g_searchPath.c_str());
    if (!g_ngxPathPtrs.empty())
    {
        info.PathListInfo.Path = g_ngxPathPtrs.data();
        info.PathListInfo.Length = (unsigned int)g_ngxPathPtrs.size();
    }

    g_device = device;
    const wchar_t* logPath = args->LogPath && args->LogPath[0] ? args->LogPath : L".";
    // Driver export uses (version, featureInfo). SDK wrapper uses (featureInfo, version).
    const char* projectId = "8e4c2a71-6b9d-4f13-9c1a-7f2e5b90d4c3";
    NVSDK_NGX_Result result = NVSDK_NGX_Result_Fail;
    if (pInitProject)
        result = pInitProject(projectId, NVSDK_NGX_ENGINE_TYPE_CUSTOM, "1.0.0", logPath, g_device, NVSDK_NGX_Version_API, &info);
    else if (pInitProjectSdk)
        result = pInitProjectSdk(projectId, NVSDK_NGX_ENGINE_TYPE_CUSTOM, "1.0.0", logPath, g_device, &info, NVSDK_NGX_Version_API);
    else
        result = pInitApp(0x244850, logPath, g_device, &info, NVSDK_NGX_Version_API);
    if (NVSDK_NGX_FAILED(result))
    {
        char buf[96];
        sprintf_s(buf, "NVSDK_NGX_D3D11_Init_with_ProjectID failed (0x%08X)", (unsigned)result);
        SetError(buf);
        DebugLogLine("%s", buf);
        return 0;
    }
    DebugLogLine("NGX D3D11 init ok");

    result = pGetCaps(&g_capabilityParams);
    if (NVSDK_NGX_FAILED(result) || !g_capabilityParams)
    {
        SetError("GetCapabilityParameters failed");
        return 0;
    }
    if (pAllocParams)
        pAllocParams(&g_evalParams);

    int available = 0;
    g_capabilityParams->Get(NVSDK_NGX_Parameter_SuperSampling_Available, &available);
    g_supported = available != 0;
    g_initialized = true;
    if (g_supported)
    {
        std::string msg = "initialized from " + Narrow(loadedFrom);
        SetError(msg.c_str());
        DebugLogLine("%s", msg.c_str());
    }
    else
    {
        SetError("NGX initialized but Super Sampling is not available");
        DebugLogLine("NGX initialized but Super Sampling is not available");
    }
    return 1;
}

extern "C" int SeDlss_IsSupported(void)
{
    return g_supported ? 1 : 0;
}

extern "C" int SeDlss_SetMode(int quality, uint32_t outWidth, uint32_t outHeight,
    uint32_t* outRenderWidth, uint32_t* outRenderHeight, float* outSharpness, int preset)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_initialized || !g_supported || !g_capabilityParams || !g_device)
    {
        SetError("NGX is not initialized");
        return 0;
    }
    if (outWidth == 0 || outHeight == 0)
    {
        SetError("invalid output size");
        return 0;
    }

    NVSDK_NGX_Parameter* params = g_capabilityParams;
    params->Set(NVSDK_NGX_Parameter_Width, outWidth);
    params->Set(NVSDK_NGX_Parameter_Height, outHeight);
    params->Set(NVSDK_NGX_Parameter_PerfQualityValue, quality);
    params->Set(NVSDK_NGX_Parameter_RTXValue, 0);
    ApplyHintPresets(params, preset);

    uint32_t renderW = outWidth;
    uint32_t renderH = outHeight;
    float sharpness = 0.0f;
    void* callback = nullptr;
    params->Get(NVSDK_NGX_Parameter_DLSSOptimalSettingsCallback, &callback);
    if (callback)
    {
        auto pfn = reinterpret_cast<PFN_NVSDK_NGX_DLSS_GetOptimalSettingsCallback>(callback);
        NVSDK_NGX_Result opt = pfn(params);
        if (!NVSDK_NGX_FAILED(opt))
        {
            params->Get(NVSDK_NGX_Parameter_OutWidth, &renderW);
            params->Get(NVSDK_NGX_Parameter_OutHeight, &renderH);
            params->Get(NVSDK_NGX_Parameter_Sharpness, &sharpness);
        }
    }

    if (renderW == 0 || renderH == 0)
    {
        SetError("DLSS optimal settings returned a zero size");
        return 0;
    }

    int flags = NVSDK_NGX_DLSS_Feature_Flags_DepthInverted | NVSDK_NGX_DLSS_Feature_Flags_AutoExposure | NVSDK_NGX_DLSS_Feature_Flags_MVLowRes;
    if (g_dlss && g_quality == quality && g_preset == preset && g_outW == outWidth && g_outH == outHeight && g_createFlags == flags)
    {
        if (outRenderWidth) *outRenderWidth = renderW;
        if (outRenderHeight) *outRenderHeight = renderH;
        if (outSharpness) *outSharpness = sharpness;
        return 1;
    }

    ReleaseDlss();
    params->Set(NVSDK_NGX_Parameter_Width, renderW);
    params->Set(NVSDK_NGX_Parameter_Height, renderH);
    params->Set(NVSDK_NGX_Parameter_OutWidth, outWidth);
    params->Set(NVSDK_NGX_Parameter_OutHeight, outHeight);
    params->Set(NVSDK_NGX_Parameter_PerfQualityValue, quality);
    ApplyHintPresets(params, preset);
    params->Set(NVSDK_NGX_Parameter_DLSS_Feature_Create_Flags, flags);
    params->Set(NVSDK_NGX_Parameter_DLSS_Enable_Output_Subrects, 0);

    ID3D11DeviceContext* ctx = nullptr;
    g_device->GetImmediateContext(&ctx);
    if (!ctx)
    {
        SetError("failed to get immediate context");
        return 0;
    }
    NVSDK_NGX_Result result = pCreateFeature(ctx, NVSDK_NGX_Feature_SuperSampling, params, &g_dlss);
    ctx->Release();
    if (NVSDK_NGX_FAILED(result) || !g_dlss)
    {
        char buf[96];
        sprintf_s(buf, "CreateFeature SuperSampling failed (0x%08X)", (unsigned)result);
        SetError(buf);
        DebugLogLine("%s", buf);
        return 0;
    }

    g_quality = quality;
    g_preset = preset;
    g_createFlags = flags;
    g_outW = outWidth;
    g_outH = outHeight;
    if (outRenderWidth) *outRenderWidth = renderW;
    if (outRenderHeight) *outRenderHeight = renderH;
    if (outSharpness) *outSharpness = sharpness;
    SetError("DLSS feature created");
    DebugLogLine("CreateFeature ok quality=%d preset=%d out=%ux%u render=%ux%u",
        quality, preset, outWidth, outHeight, renderW, renderH);
    return 1;
}

extern "C" int SeDlss_Evaluate(const SeDlssEvalArgs* args)
{
    if (!g_initialized || !g_dlss || !args || !args->DeviceContext || !args->Color || !args->Output || !args->Depth)
    {
        SetError("Evaluate missing device, color, depth, or output");
        return 0;
    }

    NVSDK_NGX_Parameter* params = g_evalParams ? g_evalParams : g_capabilityParams;
    if (!params)
    {
        SetError("no NGX parameter map");
        return 0;
    }

    auto* ctx = static_cast<ID3D11DeviceContext*>(args->DeviceContext);
    auto* color = static_cast<ID3D11Resource*>(args->Color);
    auto* depth = static_cast<ID3D11Resource*>(args->Depth);
    auto* output = static_cast<ID3D11Resource*>(args->Output);
    auto* motion = static_cast<ID3D11Resource*>(args->MotionVectors);
    if (!motion && g_device && EnsureMvTarget(g_device, args->RenderWidth, args->RenderHeight))
    {
        const float clear[4] = { 0, 0, 0, 0 };
        ctx->ClearRenderTargetView(g_mvRtv, clear);
        motion = g_mvTex;
    }

    D3D11_TEXTURE2D_DESC destDesc{};
    bool hasUav = false;
    if (!DescribeEvalDest(output, &destDesc, &hasUav))
    {
        SetError("output is not a 2D texture");
        return 0;
    }

    // DLSS writes the output as a UAV. Keen's backbuffer and BorrowRtv targets are RT+SRV only.
    ID3D11Resource* evalOutput = output;
    bool copyBack = false;
    if (!hasUav)
    {
        if (!g_device || !EnsureEvalOutput(g_device, destDesc))
        {
            DebugLogLine("EnsureEvalOutput failed dest=%ux%u fmt=%u bind=0x%x",
                destDesc.Width, destDesc.Height, (unsigned)destDesc.Format, destDesc.BindFlags);
            return 0;
        }
        evalOutput = g_evalOutTex;
        copyBack = true;
    }

    params->Reset();
    params->Set(NVSDK_NGX_Parameter_Color, color);
    params->Set(NVSDK_NGX_Parameter_Output, evalOutput);
    params->Set(NVSDK_NGX_Parameter_Depth, depth);
    if (motion)
        params->Set(NVSDK_NGX_Parameter_MotionVectors, motion);
    params->Set(NVSDK_NGX_Parameter_Jitter_Offset_X, args->JitterX);
    params->Set(NVSDK_NGX_Parameter_Jitter_Offset_Y, args->JitterY);
    params->Set(NVSDK_NGX_Parameter_Sharpness, args->Sharpness);
    params->Set(NVSDK_NGX_Parameter_Reset, args->Reset);
    params->Set(NVSDK_NGX_Parameter_MV_Scale_X, args->MvScaleX == 0.0f ? 1.0f : args->MvScaleX);
    params->Set(NVSDK_NGX_Parameter_MV_Scale_Y, args->MvScaleY == 0.0f ? 1.0f : args->MvScaleY);
    params->Set(NVSDK_NGX_Parameter_DLSS_Render_Subrect_Dimensions_Width, args->RenderWidth);
    params->Set(NVSDK_NGX_Parameter_DLSS_Render_Subrect_Dimensions_Height, args->RenderHeight);

    // Color/output/depth are often still bound as RTV/DSV from Keen's last pass.
    UnbindPipeline(ctx);

    NVSDK_NGX_Result result = pEvaluate(ctx, g_dlss, params, nullptr);
    if (NVSDK_NGX_FAILED(result))
    {
        char colorDesc[64], depthDesc[64], outputDesc[64], motionDesc[64];
        DescribeTexture(color, colorDesc, sizeof(colorDesc));
        DescribeTexture(depth, depthDesc, sizeof(depthDesc));
        DescribeTexture(evalOutput, outputDesc, sizeof(outputDesc));
        DescribeTexture(motion, motionDesc, sizeof(motionDesc));
        char buf[512];
        sprintf_s(buf,
            "EvaluateFeature failed (0x%08X %s) render=%ux%u color=%s depth=%s mv=%s output=%s",
            (unsigned)result, NgxResultName(result), args->RenderWidth, args->RenderHeight,
            colorDesc, depthDesc, motionDesc, outputDesc);
        SetError(buf);
        DebugLogLine("%s", buf);
        return 0;
    }
    if (copyBack)
        ctx->CopyResource(output, g_evalOutTex);
    if (g_debugLog)
    {
        char evalLog[256];
        sprintf_s(evalLog, "Evaluate ok render=%ux%u copyBack=%d dest=%ux%u fmt=%u bind=0x%x",
            args->RenderWidth, args->RenderHeight, copyBack ? 1 : 0,
            destDesc.Width, destDesc.Height, (unsigned)destDesc.Format, destDesc.BindFlags);
        if (strcmp(evalLog, g_lastEvalLog) != 0)
        {
            strcpy_s(g_lastEvalLog, evalLog);
            DebugLogLine("%s", evalLog);
        }
    }
    SetError("ok");
    return 1;
}

extern "C" void* SeDlss_GenerateCameraMotionVectors(const SeDlssMvArgs* args)
{
    if (!args || !args->Device || !args->DeviceContext || !args->Depth || args->Width == 0 || args->Height == 0)
    {
        SetError("motion-vector args are incomplete");
        return nullptr;
    }

    auto* device = static_cast<ID3D11Device*>(args->Device);
    auto* ctx = static_cast<ID3D11DeviceContext*>(args->DeviceContext);
    if (!EnsureMvShaders(device) || !EnsureMvTarget(device, args->Width, args->Height))
        return nullptr;

    struct Cb
    {
        float InvViewProj[16];
        float UnjitteredViewProj[16];
        float PrevViewProj[16];
        float RenderSize[2];
        float InvRenderSize[2];
    } cb{};
    memcpy(cb.InvViewProj, args->InvViewProj, sizeof(cb.InvViewProj));
    memcpy(cb.UnjitteredViewProj, args->UnjitteredViewProj, sizeof(cb.UnjitteredViewProj));
    memcpy(cb.PrevViewProj, args->PrevViewProj, sizeof(cb.PrevViewProj));
    cb.RenderSize[0] = (float)args->Width;
    cb.RenderSize[1] = (float)args->Height;
    cb.InvRenderSize[0] = 1.0f / (float)args->Width;
    cb.InvRenderSize[1] = 1.0f / (float)args->Height;

    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(ctx->Map(g_mvCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        return nullptr;
    memcpy(mapped.pData, &cb, sizeof(cb));
    ctx->Unmap(g_mvCb, 0);

    // Unbind Keen's depth DSV before sampling the same texture.
    ID3D11RenderTargetView* nullRtvs[1] = { nullptr };
    ctx->OMSetRenderTargets(1, nullRtvs, nullptr);

    auto* depthRes = static_cast<ID3D11Resource*>(args->Depth);
    D3D11_TEXTURE2D_DESC depthDesc{};
    QueryTexture2D(depthRes, &depthDesc);
    if (!EnsureDepthSrv(device, depthRes, depthDesc, g_cachedDepthRes, g_cachedDepthSrv))
    {
        SetError("failed to create depth SRV for motion vectors");
        DebugLogLine("depth SRV failed fmt=%u", (unsigned)depthDesc.Format);
        return nullptr;
    }

    D3D11_VIEWPORT vp{};
    vp.Width = (float)args->Width;
    vp.Height = (float)args->Height;
    vp.MaxDepth = 1.0f;
    ctx->RSSetViewports(1, &vp);
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(g_mvVs, nullptr, 0);
    ctx->VSSetConstantBuffers(0, 1, &g_mvCb);
    ctx->PSSetConstantBuffers(0, 1, &g_mvCb);
    ctx->PSSetSamplers(0, 1, &g_mvSampler);

    ctx->OMSetRenderTargets(1, &g_mvRtv, nullptr);
    ctx->PSSetShader(g_mvPs, nullptr, 0);
    ctx->PSSetShaderResources(0, 1, &g_cachedDepthSrv);
    ctx->Draw(3, 0);

    ID3D11ShaderResourceView* nullSrv[1] = {};
    ctx->PSSetShaderResources(0, 1, nullSrv);
    ctx->OMSetRenderTargets(0, nullptr, nullptr);
    return g_mvTex;
}

extern "C" int SeDlss_UpsampleDepth(void* devicePtr, void* contextPtr, void* srcDepth, void* destDepth)
{
    if (!devicePtr || !contextPtr || !srcDepth || !destDepth)
    {
        SetError("depth upsample args are incomplete");
        return 0;
    }

    auto* device = static_cast<ID3D11Device*>(devicePtr);
    auto* ctx = static_cast<ID3D11DeviceContext*>(contextPtr);
    auto* srcRes = static_cast<ID3D11Resource*>(srcDepth);
    auto* destRes = static_cast<ID3D11Resource*>(destDepth);
    if (!EnsureDepthUpsample(device))
        return 0;

    D3D11_TEXTURE2D_DESC srcDesc{};
    D3D11_TEXTURE2D_DESC destDesc{};
    if (!QueryTexture2D(srcRes, &srcDesc) || !QueryTexture2D(destRes, &destDesc))
    {
        SetError("depth upsample textures are not Texture2D");
        return 0;
    }

    ID3D11RenderTargetView* nullRtvs[1] = { nullptr };
    ctx->OMSetRenderTargets(1, nullRtvs, nullptr);

    if (!EnsureDepthSrv(device, srcRes, srcDesc, g_cachedUpSrcRes, g_cachedUpSrcSrv))
    {
        SetError("failed to create depth upsample source SRV");
        return 0;
    }

    if (!g_cachedUpDestDsv || g_cachedUpDestRes != destRes)
    {
        if (g_cachedUpDestDsv)
        {
            g_cachedUpDestDsv->Release();
            g_cachedUpDestDsv = nullptr;
        }
        g_cachedUpDestRes = destRes;
        D3D11_DEPTH_STENCIL_VIEW_DESC dsvDesc{};
        dsvDesc.Format = DepthDsvFormat(destDesc.Format);
        dsvDesc.ViewDimension = D3D11_DSV_DIMENSION_TEXTURE2D;
        if (FAILED(device->CreateDepthStencilView(destRes, &dsvDesc, &g_cachedUpDestDsv)))
        {
            g_cachedUpDestDsv = nullptr;
            SetError("failed to create depth upsample dest DSV");
            return 0;
        }
    }

    D3D11_VIEWPORT vp{};
    vp.Width = (float)destDesc.Width;
    vp.Height = (float)destDesc.Height;
    vp.MaxDepth = 1.0f;
    ctx->OMSetRenderTargets(0, nullptr, g_cachedUpDestDsv);
    ctx->OMSetDepthStencilState(g_depthWriteAlways, 0);
    ctx->RSSetViewports(1, &vp);
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(g_depthVs, nullptr, 0);
    ctx->PSSetShader(g_depthPs, nullptr, 0);
    ctx->PSSetShaderResources(0, 1, &g_cachedUpSrcSrv);
    ctx->PSSetSamplers(0, 1, &g_depthSampler);
    ctx->Draw(3, 0);

    ID3D11ShaderResourceView* nullSrv = nullptr;
    ctx->PSSetShaderResources(0, 1, &nullSrv);
    ctx->OMSetRenderTargets(0, nullptr, nullptr);
    ctx->OMSetDepthStencilState(nullptr, 0);
    SetError("ok");
    return 1;
}

extern "C" void SeDlss_Shutdown(void)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    ReleaseDlss();
    ReleaseMvPipeline();
    ReleaseEvalOutput();
    ReleaseMvShaders();
    ReleaseDepthUpsample();
    ReleaseCachedViews();
    if (g_evalParams && pDestroyParams)
    {
        pDestroyParams(g_evalParams);
        g_evalParams = nullptr;
    }
    g_capabilityParams = nullptr;
    if (g_initialized && pShutdown1)
        pShutdown1(g_device);
    g_initialized = false;
    g_supported = false;
    g_device = nullptr;
    if (g_ngx)
    {
        FreeLibrary(g_ngx);
        g_ngx = nullptr;
    }
    SetError("shutdown");
    CloseDebugLog();
}

extern "C" const char* SeDlss_LastError(void)
{
    return g_lastError.c_str();
}
