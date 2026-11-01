// nn::vi (display layer): on a real device, the vi service is used to open the display, create a layer and get the native window handle; the handle is passed to NVN's window builder.
// Host: returns host static objects; when hooking up Metal, the native window corresponds to the front end's CAMetalLayer (front_metal_layer()).
#include "../host/hle.h"
#include <stdatomic.h>

static struct { uint32_t magic; } g_display = {0x50534944}, g_layer = {0x5259414c};
static struct { uint32_t magic; void *layer; } g_native = {0x4e49574e, 0};

HLE_FORCE("_ZN2nn2vi10InitializeEv", "display service init; the host has no vi service", void, h_vi_Init, void) {}
HLE_FORCE("_ZN2nn2vi18OpenDefaultDisplayEPPNS0_7DisplayE", "open default display; the host returns a static object", Result, h_vi_OpenDisp, void **out) {
    *out = &g_display; return RES_OK;
}
HLE_FORCE("_ZN2nn2vi11CreateLayerEPPNS0_5LayerEPNS0_7DisplayE", "create layer; the host returns a static object", Result, h_vi_CreateLayer, void **out, void *disp) {
    (void)disp; *out = &g_layer; return RES_OK;
}
// The overload with the layer's size (SDL3's Switch video driver, Absolum: 1920x1080 docked); the SDK's version calls through the vi service object the host never made (SIGSEGV at +0xe4).
// An NVN or Vulkan window's textures decide what is presented; an EGL window surface takes the layer's size (dm/gfx/gl/hle_gl.m: Digimon Story Cyber Sleuth sizes its layer
// itself and draws into the surface at that size)
static _Atomic uint32_t g_layer_wh;   // w | h << 16 of the last sized CreateLayer, 0 = none
void dm_vi_layer_size(int *w, int *h) { uint32_t v = g_layer_wh; *w = (int)(v & 0xffff); *h = (int)(v >> 16); }
HLE_FORCE("_ZN2nn2vi11CreateLayerEPPNS0_5LayerEPNS0_7DisplayEii", "create layer of a given size; the host returns a static object", Result, h_vi_CreateLayerWH, void **out, void *disp, int w, int h) {
    (void)disp; LOG("vi::CreateLayer %dx%d", w, h); *out = &g_layer;
    if (w > 0 && h > 0 && w < 0x10000 && h < 0x10000) g_layer_wh = (uint32_t)w | (uint32_t)h << 16;
    return RES_OK;
}
HLE_FORCE("_ZN2nn2vi15GetNativeWindowEPPvPNS0_5LayerE", "native window handle, passed to the NVN window builder; the host returns a static object", Result, h_vi_GetNative, void **out, void *layer) {
    (void)layer; *out = &g_native; return RES_OK;
}
// Link's Awakening also crops its layer and tears the display down on exit; the SDK's versions dereference the vi service objects the host never made (SetLayerCrop: SIGSEGV at +0x64).
// The crop is kept for the present: the Vulkan present (hle_vk.m), where Super Mario Galaxy keeps a 1920x1080 swapchain in handheld mode, draws into its top-left 1280x720 and
// crops the layer to that every frame; and the NVN present when the game sets no window crop of its own (nvnWindowSetCrop; present_crop in hle_nvn.c), where Link's Awakening
// draws 1280x720 into its 1920x1080 window textures in handheld mode and crops the layer to that. Logged only when it changes
static _Atomic uint64_t g_crop;   // x | y << 16 | w << 32 | h << 48, 0 = none
void dm_vi_layer_crop(int c[4]) { uint64_t v = g_crop; c[0] = (int)(v & 0xffff); c[1] = (int)(v >> 16 & 0xffff); c[2] = (int)(v >> 32 & 0xffff); c[3] = (int)(v >> 48); }
HLE_FORCE("_ZN2nn2vi12SetLayerCropEPNS0_5LayerEiiii", "layer crop; the host records it and the present crops by it", Result, h_vi_SetCrop, void *layer, int x, int y, int w, int h) {
    (void)layer;
    uint64_t v = x < 0 || y < 0 || w <= 0 || h <= 0 || x > 0xffff || y > 0xffff || w > 0xffff || h > 0xffff ? 0 : (uint64_t)x | (uint64_t)y << 16 | (uint64_t)w << 32 | (uint64_t)h << 48;
    if (atomic_exchange(&g_crop, v) != v) LOG("vi::SetLayerCrop %d,%d %dx%d", x, y, w, h);
    return RES_OK;
}
// Super Mario Odyssey sets its layer's scaling mode; the SDK's version uses the vi service objects the host never made
HLE_FORCE("_ZN2nn2vi19SetLayerScalingModeEPNS0_5LayerENS0_11ScalingModeE", "layer scaling mode; the host present does its own scaling", Result, h_vi_SetScaling, void *layer, int mode) {
    (void)layer; LOG("vi::SetLayerScalingMode %d (ignored)", mode); return RES_OK;
}
// Unreal Engine paces its frames by the layer's frame number (Octopath Traveler 0, main+0x10140e8); the SDK's versions read the Android surface of the vi service the host never made
// (SIGSEGV in android::Surface::getQueuedFrameNumber). The host's frame number is the count of frames the game has presented
uint64_t dm_nvn_frame_count(void);
HLE_FORCE("_ZN2nn2vi20GetLatestFrameNumberEPmPKNS0_5LayerE", "the layer's latest frame number: the frames presented so far", Result, h_vi_FrameNo, uint64_t *out, const void *layer) {
    (void)layer; *out = dm_nvn_frame_count(); return RES_OK;
}
HLE_FORCE("_ZN2nn2vi16ListFrameHistoryEPNS0_9FrameInfoEiPKNS0_5LayerE", "the layer's frame history: none kept, 0 entries", int, h_vi_FrameHist, void *out, int n, const void *layer) {
    (void)out; (void)layer; static int once; if (!once++) LOG("vi::ListFrameHistory (%d entries asked): the host keeps no frame history, 0 returned", n);
    return 0;
}
// Rhythm Heaven Groove (SDK 21) asks for the built-in panel's identity at start (main+0xa9a4); the SDK's version calls through the vi service object the host never made (SIGSEGV at +0x20).
//   The game reads a u32 kind and, when it is 2 or more, 0x14 more bytes that it hands on; any other answer makes it hand on zeros. The host has no panel: an all-zero identifier, kind 0
HLE_FORCE("_ZN2nn2vi32GetDefaultDisplayPanelIdentifierEPNS0_15PanelIdentifierE", "built-in panel identity; the host has no panel: all zero", Result, h_vi_PanelId, void *out) {
    memset(out, 0, 0x18); return RES_OK;
}
// The Great Ace Attorney Chronicles (SDK 10) lists up to 10 displays before opening the default one (main+0x808f08) and does not read the list. DisplayInfo is 0x60 bytes:
//   name[0x40], bool has a layer limit at +0x40, then u64 layer count limit, largest layer width and height. One display, "Default", as Horizon lists for an application
HLE_FORCE("_ZN2nn2vi12ListDisplaysEPNS0_11DisplayInfoEi", "list displays; the host has one, \"Default\"", int, h_vi_ListDisp, void *out, int n) {
    if (n <= 0 || !out) return 0;
    uint8_t *p = out; memset(p, 0, 0x60); strcpy((char *)p, "Default");
    p[0x40] = 1; uint64_t lim[3] = {1, 1920, 1080}; memcpy(p + 0x48, lim, sizeof lim);
    return 1;
}
HLE_FORCE("_ZN2nn2vi12DestroyLayerEPNS0_5LayerE", "destroy layer; host static object", void, h_vi_DestroyLayer, void *layer) { (void)layer; }
HLE_FORCE("_ZN2nn2vi12CloseDisplayEPNS0_7DisplayE", "close display; host static object", void, h_vi_CloseDisp, void *disp) { (void)disp; }
HLE_FORCE("_ZN2nn2vi8FinalizeEv", "display service shutdown; the host has no vi service", void, h_vi_Fini, void) {}

// HDR, VRR and the extended swap interval (Switch 2's SDK, 20.5.x). Donkey Kong Bananza (SDK 20.5.19) crashed at main+0x2036f4 (SIGSEGV pc=0) on the first
// SetLayerNextHdrContentGamutMetadata: the sdk's versions of these functions test their Display argument against the sdk's own default display object and tail-call through
// the vi service object behind the layer (nn::vi::Layer::SetNextHdr...), which the host's static stand-in Display and Layer never made (the SetLayerCrop mechanism above).
//   Contract, read from the sdk and from Bananza's window update (main+0x203728, +0x20496c, +0x204c60): every Query* and Set*Capability returns an nn::Result (0 = success, 0x272 for
//   a Display that is not the default one); the Set Layer* HDR functions and the swap interval ones are Results too (their callers ignore them). Bananza asks QueryDisplayVrrSupported and
//   QueryDisplayHdrStatus each frame it updates its window; a "not supported" answer makes it call SetDisplayVrrCapability(false) and ClearLayerExtendedSwapInterval once and fall back
//   to the NVN window's present interval (the 0x204c30 setter, nvnWindowSetPresentInterval) for its 30 fps cap. It sets SetLayerExtendedSwapInterval(layer, 1, fps) only when VRR is
//   reported supported.
// VRR: reported supported when the host can show frames off the 60 Hz grid, a screen faster than 60 Hz (ProMotion) with the display link presenting (mtl_vrr_capable, nvn_mtl.m;
//   DM_VRR=0 / 1 overrides). With it Bananza runs another frame-rate controller (main+0x204640, called from +0x2036b4 only while VRR is on): fps = 60 / (frame cost in vsyncs + margin),
//   clamped and stepped; at 40 fps and up (setter +0x204934) it calls SetDisplayVrrCapability(true) and SetLayerExtendedSwapInterval(layer, 1, fps) and paces itself, sleeping until
//   1 / fps after its frame start (+0x20430c), with present interval 1; below 40 it clears both and sets present interval ceil(60 / fps) as without VRR. While the extended interval is
//   set, the display link runs at 120 Hz and takes each frame on the next refresh (dm_vi_vrr_fps; link_sync in nvn_mtl.m), instead of holding it to a 60 Hz vsync.
// HDR (HDR10 through EDR): reported when the window's screen can show more than SDR white (mtl_hdr_peak, nvn_mtl.m; DM_HDR=0 / 1 overrides), with that screen's peak luminance and the
//   BT.2408 reference white, 203 nits, as the paper white. On it Bananza re-creates its NVN window as RGB10A2 (format 0x3d, was RGBA8 0x25) and its last pass (fs fbc8d9ef451aabed) writes
//   PQ (SMPTE ST 2084) in BT.2020 (SetLayerNextHdrContentGamutMetadata 1), scaled so that paper white is the reported paper white and clipped to the luminance ranges it sets
//   (SetLayerNext*ContentLuminanceRangeMetadata: standard 0..203, extended 0..peak). The present then shows that window through a PQ / BT.2020 layer with HDR10 metadata
//   (nvn_mtl.m hdr_layer_sync): macOS maps 203 nits to SDR white and tone-maps the rest to the screen's headroom. Its own SDR fallback (an RGBA8 window) follows when the answer turns SDR
//   (the window moved to an SDR screen): the game asks each window update.
static _Atomic uint32_t g_hdr_gamut = ~0u, g_hdr_tonemap = ~0u, g_hdr_std = ~0u, g_hdr_ext = ~0u;   // last values the game set, ~0 = none yet
static _Atomic uint32_t g_vrr_cap = ~0u, g_ext_swap = ~0u;   // the game's VRR capability flag; the extended swap interval (a | b << 16), ~0 = none/cleared
static _Atomic int g_hdr_on;   // what QueryDisplayHdrStatus last answered
int dm_vi_hdr_on(void) { return g_hdr_on; }
uint32_t dm_vi_hdr_metadata(int which) { return which == 0 ? g_hdr_gamut : which == 1 ? g_hdr_tonemap : which == 2 ? g_hdr_std : g_hdr_ext; }
uint32_t dm_vi_vrr_capability(void) { return g_vrr_cap; }
// The frame rate the game paces itself to while it uses VRR (the extended swap interval's second value), 0 when it does not
int dm_vi_vrr_fps(void) { uint32_t s = g_ext_swap; return g_vrr_cap == 1 && s != ~0u ? (int)(s >> 16) : 0; }
__attribute__((weak)) int mtl_vrr_capable(void) { return 0; }   // nvn_mtl.m; graphics unit tests without it have no VRR
__attribute__((weak)) int mtl_hdr_peak(void) { return 0; }   // nvn_mtl.m: the screen's peak luminance in nits when it can show HDR, 0 = SDR; graphics unit tests have none
#define HDR_PAPER_WHITE 203   // nits: ITU-R BT.2408's reference white for HDR10, which the HDR10 layer metadata maps to SDR white (nvn_mtl.m)
// HdrOutputFormat: not read by Bananza (a stack local it never uses); 0 is written. The u16 is the screen's peak luminance in nits (the game takes its minimum with a cap of its own; times
// the paper-white scale it becomes the layer's luminance range); 100, SDR white, when not HDR
HLE_FORCE("_ZN2nn2vi21QueryDisplayHdrStatusEPbPNS0_15HdrOutputFormatEPtPKNS0_7DisplayE", "HDR status: HDR10 when the window's screen has EDR headroom", Result, h_vi_HdrStatus, uint8_t *hdr, int *format, uint16_t *nits, const void *disp) {
    (void)disp; int peak = mtl_hdr_peak(), on = peak > 0;
    if (hdr) *hdr = (uint8_t)on; if (format) *format = 0; if (nits) *nits = (uint16_t)(on ? peak : 100);
    if (atomic_exchange(&g_hdr_on, on) != on) LOG("vi::QueryDisplayHdrStatus: %s", on ? "HDR (the window's screen has EDR headroom; HDR10 through EDR)" : "SDR");
    static _Atomic int was_peak; if (on && atomic_exchange(&was_peak, peak) != peak) LOG("vi::QueryDisplayHdrStatus: peak %d nits, paper white %d nits", peak, HDR_PAPER_WHITE);
    return RES_OK;
}
HLE_FORCE("_ZN2nn2vi25QueryDisplayHdrPaperWhiteEPtPKNS0_7DisplayE", "HDR paper white: BT.2408's 203 nits in HDR, 100 in SDR", Result, h_vi_HdrPaperWhite, uint16_t *nits, const void *disp) {
    (void)disp; if (nits) *nits = g_hdr_on ? HDR_PAPER_WHITE : 100; return RES_OK;
}
HLE_FORCE("_ZN2nn2vi35SetLayerNextHdrContentGamutMetadataEPNS0_5LayerENS0_15HdrContentGamutE", "HDR content gamut; the present picks the layer's color space from it", Result, h_vi_HdrGamut, void *layer, int gamut) {
    (void)layer; if (atomic_exchange(&g_hdr_gamut, (uint32_t)gamut) != (uint32_t)gamut) LOG("vi::SetLayerNextHdrContentGamutMetadata %d", gamut);
    return RES_OK;
}
HLE_FORCE("_ZN2nn2vi39SetLayerNextHdrToSdrToneMappingMetadataEPNS0_5LayerENS0_14HdrToneMappingE", "HDR to SDR tone mapping; kept and logged (macOS tone-maps the HDR10 layer itself)", Result, h_vi_HdrTone, void *layer, int mode) {
    (void)layer; if (atomic_exchange(&g_hdr_tonemap, (uint32_t)mode) != (uint32_t)mode) LOG("vi::SetLayerNextHdrToSdrToneMappingMetadata %d (kept)", mode);
    return RES_OK;
}
HLE_FORCE("_ZN2nn2vi52SetLayerNextHdrExtendedContentLuminanceRangeMetadataEPNS0_5LayerEtt", "HDR extended content luminance range: the HDR10 layer metadata's maximum", Result, h_vi_HdrExt, void *layer, unsigned a, unsigned b) {
    (void)layer; a &= 0xffff; b &= 0xffff; uint32_t v = a | b << 16;
    if (atomic_exchange(&g_hdr_ext, v) != v) LOG("vi::SetLayerNextHdrExtendedContentLuminanceRangeMetadata %u %u", a, b);
    return RES_OK;
}
HLE_FORCE("_ZN2nn2vi52SetLayerNextHdrStandardContentLuminanceRangeMetadataEPNS0_5LayerEtt", "HDR standard content luminance range; kept and logged", Result, h_vi_HdrStd, void *layer, unsigned a, unsigned b) {
    (void)layer; a &= 0xffff; b &= 0xffff; uint32_t v = a | b << 16;
    if (atomic_exchange(&g_hdr_std, v) != v) LOG("vi::SetLayerNextHdrStandardContentLuminanceRangeMetadata %u %u", a, b);
    return RES_OK;
}
// Asked each window update: a change (the window moved to another screen) switches the game's controller at its next update
HLE_FORCE("_ZN2nn2vi24QueryDisplayVrrSupportedEPbPKNS0_7DisplayE", "VRR support: when the screen and the display link can show frames off the 60 Hz grid", Result, h_vi_VrrSupported, uint8_t *out, const void *disp) {
    (void)disp; int on = mtl_vrr_capable() != 0; if (out) *out = (uint8_t)on;
    static _Atomic int was = -1; if (atomic_exchange(&was, on) != on) LOG("vi::QueryDisplayVrrSupported: %s", on ? "yes (the display link follows the game's frames on a screen faster than 60 Hz)" : "no (no screen faster than 60 Hz, or the display link is off)");
    return RES_OK;
}
HLE_FORCE("_ZN2nn2vi23SetDisplayVrrCapabilityEPNS0_7DisplayEb", "the game's VRR capability flag", void, h_vi_VrrCap, void *disp, int on) {
    (void)disp; on &= 1; if (atomic_exchange(&g_vrr_cap, (uint32_t)on) != (uint32_t)on) LOG("vi::SetDisplayVrrCapability %d", on);
}
// SetLayerExtendedSwapInterval(layer, 1, fps): the game's frame cap while it uses VRR; it paces itself to it, the display link takes each frame on the next refresh
HLE_FORCE("_ZN2nn2vi28SetLayerExtendedSwapIntervalEPNS0_5LayerEii", "extended swap interval: the game's frame cap under VRR", Result, h_vi_ExtSwap, void *layer, int a, int b) {
    (void)layer; uint32_t v = (uint32_t)(a & 0xffff) | (uint32_t)b << 16;
    if (atomic_exchange(&g_ext_swap, v) != v) LOG("vi::SetLayerExtendedSwapInterval %d %d", a, b);
    return RES_OK;
}
HLE_FORCE("_ZN2nn2vi30ClearLayerExtendedSwapIntervalEPNS0_5LayerE", "clear the extended swap interval; kept and logged", Result, h_vi_ExtSwapClear, void *layer) {
    (void)layer; if (atomic_exchange(&g_ext_swap, ~0u) != ~0u) LOG("vi::ClearLayerExtendedSwapInterval");
    return RES_OK;
}
