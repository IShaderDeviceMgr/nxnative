// nn::streamplay (Switch 2 GameShare): the console streams the game to other consoles nearby or online, and their players ("guests") send input back.
// Found on Donkey Kong Bananza (Switch 2, SDK 20.5.19); Pokemon Pokopia imports it too. The SDK's public functions all go through one session to service stpl:u
// (detail::CreateUserServiceByHipc, first reached from GetRequiredMemorySize at sdk+0x6ac108), which the host does not serve: [RED] sf: the game connects to service stpl:u itself, then
// nn::diag abort Result 0xe15 at sdk+0x6b6440. The host never streams, so the answer is always "no streaming session, no guests, never connects":
//  - GetRequiredMemorySize returns one page (the game allocates max(sizes) with 0x1000 alignment, main+0x5471e8) and Initialize succeeds without touching the buffer (the game ShowErrors a non-zero Result);
//  - GetConnectionState writes the idle state (the game reads the first word as a bit set of connection kinds and the second as a Result: both zero);
//  - GetConnectionStateChangedEvent is a SystemEvent bound to a kernel event and never signalled (the game polls it with TryWaitSystemEvent);
//  - OpenSessionWithMatchmakeSupport returns the SDK's own "the user cancelled the matchmaking applet" Result 0x3d1f, which the game's wrapper (main+0x186f4a8) swallows silently; StartStreaming and CloseSession
//    are void and abort on failure in the SDK: no-ops, which the game reaches only after a session was opened (a user action that never succeeds);
//  - every guest getter reports no guest pad: style set empty, state / handle counts 0, handles zero. Even the SDK's local-only getters (GetGuestPadStates ...) are hooked, because they abort on an
//    invalid GuestPadId and read shared memory that only the real Initialize maps.
// Not hooked: the GuestVibrationTarget constructor (it only initialises its own object and the SDK's VibrationMixer).
#include "hle.h"

static _Alignas(16) uint8_t g_ev_conn[0x100];
static int g_joy_hold;   // Get/SetGuestNpadJoyHoldType: kept for the getter; 0 = Vertical, as the SDK starts

HLE_FORCE("_ZN2nn10streamplay21GetRequiredMemorySizeENS0_14StreamPlayTypeENS0_13StreamingModeEi", "GameShare memory size; stpl:u is not served, one page (page aligned, not used)", int, h_sp_ReqMem, int type, int mode, int n) { (void)type; (void)mode; (void)n; return 0x1000; }
HLE_FORCE("_ZN2nn10streamplay10InitializeENS0_14StreamPlayTypeENS0_13StreamingModeEPvm", "GameShare initialize (connects to stpl:u); succeeds, the buffer is not used", Result, h_sp_Init, int type, int mode, void *buf, size_t size) { (void)type; (void)mode; (void)buf; (void)size; return RES_OK; }
HLE_FORCE("_ZN2nn10streamplay8FinalizeEv", "GameShare finalize; the SDK aborts when it was not initialised by it", void, h_sp_Fin, void) {}
HLE_FORCE("_ZN2nn10streamplay31OpenSessionWithMatchmakeSupportEPNS0_11SessionInfoERKNS0_15MatchmakeConfigE", "GameShare matchmaking applet; no streaming: the user-cancelled Result the game ignores", Result, h_sp_Open, void *info, const void *cfg) { (void)info; (void)cfg; return 0x3d1f; }
HLE_FORCE("_ZN2nn10streamplay12CloseSessionEv", "GameShare session close; no session", void, h_sp_Close, void) {}
HLE_FORCE("_ZN2nn10streamplay14StartStreamingEv", "GameShare start streaming; the host never streams", void, h_sp_Start, void) {}
HLE_FORCE("_ZN2nn10streamplay18GetConnectionStateEPNS0_15ConnectionStateE", "GameShare connection state: idle (no connection, no error)", void, h_sp_State, uint64_t *st) { if (st) *st = 0; }
HLE_FORCE("_ZN2nn10streamplay30GetConnectionStateChangedEventEv", "GameShare connection change event: bound to a kernel event, never signalled", void *, h_sp_StateEv, void) { hle_bind_system_event(g_ev_conn, 0); return g_ev_conn; }

HLE_FORCE("_ZN2nn10streamplay25SetSupportedHidFeatureSetENS_4util10BitFlagSetILi32ENS0_13HidFeatureTagEEE", "GameShare supported HID features; no guests", void, h_sp_SetHid, uint32_t set) { (void)set; }
HLE_FORCE("_ZN2nn10streamplay29SetSupportedGuestNpadStyleSetENS_4util10BitFlagSetILi32ENS_3hid12NpadStyleTagEEE", "GameShare supported guest npad styles; no guests", void, h_sp_SetStyles, uint32_t set) { (void)set; }
HLE_FORCE("_ZN2nn10streamplay23SetGuestNpadJoyHoldTypeENS_3hid15NpadJoyHoldTypeE", "GameShare guest Joy-Con hold type; kept for the getter", void, h_sp_SetHold, int t) { g_joy_hold = t; }
HLE_FORCE("_ZN2nn10streamplay23GetGuestNpadJoyHoldTypeEv", "GameShare guest Joy-Con hold type", int, h_sp_GetHold, void) { return g_joy_hold; }
HLE_FORCE("_ZN2nn10streamplay19GetGuestPadStyleSetERKNS0_10GuestPadIdE", "GameShare guest pad styles: none", uint32_t, h_sp_PadStyle, const void *id) { (void)id; return 0; }

#define SP_PADSTATES(sym, cname) \
    HLE_FORCE("_ZN2nn10streamplay17GetGuestPadStatesEPNS0_13GuestPadStateINS_3hid" sym "EEEiRKNS0_10GuestPadIdE", "GameShare guest pad states: no guests, 0 states", int, cname, void *out, int n, const void *id) { (void)out; (void)n; (void)id; return 0; }
SP_PADSTATES("16NpadFullKeyState", h_sp_PadFull)
SP_PADSTATES("16NpadJoyDualState", h_sp_PadDual)
SP_PADSTATES("16NpadJoyLeftState", h_sp_PadLeft)
SP_PADSTATES("17NpadHandheldState", h_sp_PadHand)
SP_PADSTATES("17NpadJoyRightState", h_sp_PadRight)

HLE_FORCE("_ZN2nn10streamplay18GetGuestJcmsHandleERKNS0_10GuestPadIdERKNS_3hid8JcmsTypeE", "GameShare guest Joy-Con mouse handle: none (zero)", uint64_t, h_sp_JcmsH, const void *id, const void *type) { (void)id; (void)type; return 0; }
HLE_FORCE("_ZN2nn10streamplay18GetGuestJcmsStatesEPNS0_14GuestJcmsStateEiRKNS0_15GuestJcmsHandleE", "GameShare guest Joy-Con mouse states: 0", int, h_sp_JcmsS, void *out, int n, const void *h) { (void)out; (void)n; (void)h; return 0; }
HLE_FORCE("_ZN2nn10streamplay28GetGuestSixAxisSensorHandlesEPNS0_24GuestSixAxisSensorHandleEiRKNS0_10GuestPadIdENS_4util10BitFlagSetILi32ENS_3hid12NpadStyleTagEEE", "GameShare guest six-axis handles: none", int, h_sp_SixH, void *out, int n, const void *id, uint32_t styles) { (void)out; (void)n; (void)id; (void)styles; return 0; }
HLE_FORCE("_ZN2nn10streamplay27GetGuestSixAxisSensorStatesEPNS0_23GuestSixAxisSensorStateEiRKNS0_24GuestSixAxisSensorHandleE", "GameShare guest six-axis states: 0", int, h_sp_SixS, void *out, int n, const void *h) { (void)out; (void)n; (void)h; return 0; }
HLE_FORCE("_ZN2nn10streamplay30GetGuestVibrationDeviceHandlesEPNS0_26GuestVibrationDeviceHandleEiRKNS0_10GuestPadIdENS_4util10BitFlagSetILi32ENS_3hid12NpadStyleTagEEE", "GameShare guest vibration handles: none", int, h_sp_VibH, void *out, int n, const void *id, uint32_t styles) { (void)out; (void)n; (void)id; (void)styles; return 0; }
HLE_FORCE("_ZN2nn10streamplay27GetGuestVibrationDeviceInfoEPNS_3hid19VibrationDeviceInfoERKNS0_26GuestVibrationDeviceHandleE", "GameShare guest vibration device info: no device (zeroed, never reached without a handle)", Result, h_sp_VibI, uint64_t *info, const void *h) { (void)h; if (info) *info = 0; return RES_OK; }
HLE_FORCE("_ZN2nn10streamplay20GuestVibrationTarget29SetGuestVibrationDeviceHandleERKNS0_26GuestVibrationDeviceHandleE", "GameShare guest vibration target handle; no guests, nothing is sent", void, h_sp_VtSet, void *self, const void *h) { (void)self; (void)h; }
HLE_FORCE("_ZN2nn10streamplay20GuestVibrationTarget31UnsetGuestVibrationDeviceHandleEv", "GameShare guest vibration target handle cleared; nothing is sent", void, h_sp_VtUnset, void *self) { (void)self; }
