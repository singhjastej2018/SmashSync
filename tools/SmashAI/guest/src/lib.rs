use smash::app::{self, lua_bind::*};
use smash::lib::lua_const::*;
use skyline::hooks::{self, Region};
use std::time::Duration;

const MAX_FIGHTERS: usize = 3;
const PROTOCOL_VERSION: u32 = 2;

const FLAG_IN_MATCH: u32 = 1 << 0;
const FLAG_DEAD_BASE: u32 = 8;
const FLAG_REBIRTH_BASE: u32 = 12;
const FLAG_ENTRY_BASE: u32 = 16;

// Protocol-v2 host control fields are appended after the v1 288-byte payload.
// The host may write gate_enabled/step_budget. The guest owns gate_epoch/waiting.
const CONTROL_DISABLED: u32 = 0;
const CONTROL_ENABLED: u32 = 1;

// Signature for SSBU's once-per-game-frame function. This is searched at runtime
// instead of using a fixed game-version offset, so the exporter does not need
// Smashline and is less brittle across nearby SSBU updates.
const ONCE_PER_GAME_FRAME_PATTERN: &[u8] = &[
    0xff, 0xc3, 0x01, 0xd1, // sub sp, sp, #0x70
    0xfb, 0x0b, 0x00, 0xf9, // str x27, [sp, #0x10]
    0xfa, 0x67, 0x02, 0xa9, // stp x26, x25, [sp, #0x20]
    0xf8, 0x5f, 0x03, 0xa9, // stp x24, x23, [sp, #0x30]
    0xf6, 0x57, 0x04, 0xa9, // stp x22, x21, [sp, #0x40]
    0xf4, 0x4f, 0x05, 0xa9, // stp x20, x19, [sp, #0x50]
    0xfd, 0x7b, 0x06, 0xa9, // stp x29, x30, [sp, #0x60]
    0xfd, 0x83, 0x01, 0x91, // add x29, sp, #0x60
    0x0b, 0x90, 0x40, 0xf9, // ldr x11, [x0, #0x120]
];

extern "C" {
    #[link_name = "\u{1}_ZN3app14sv_information8stage_idEv"]
    fn stage_id() -> i32;

    #[link_name = "\u{1}_ZN3app14sv_information27get_remaining_time_as_frameEv"]
    fn get_remaining_time_as_frame() -> u32;
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct FighterState {
    pub sample_frame: u64,
    pub present: u32,
    pub fighter_kind: i32,
    pub status_kind: i32,
    pub situation_kind: i32,
    pub stocks: i32,
    pub is_cpu: u32,

    pub x: f32,
    pub y: f32,
    pub speed_x: f32,
    pub speed_y: f32,
    pub facing: f32,
    pub percent: f32,

    pub motion_kind: u64,
    pub motion_frame: f32,
    pub motion_end_frame: f32,

    pub jumps_used: i32,
    pub jumps_max: i32,
}

impl FighterState {
    pub const EMPTY: Self = Self {
        sample_frame: 0,
        present: 0,
        fighter_kind: -1,
        status_kind: 0,
        situation_kind: 0,
        stocks: 0,
        is_cpu: 0,
        x: 0.0,
        y: 0.0,
        speed_x: 0.0,
        speed_y: 0.0,
        facing: 1.0,
        percent: 0.0,
        motion_kind: 0,
        motion_frame: 0.0,
        motion_end_frame: 0.0,
        jumps_used: 0,
        jumps_max: 0,
    };
}

#[repr(C)]
pub struct SharedState {
    // First 24 bytes are the host discovery header.
    pub magic: [u8; 8],
    pub protocol_version: u32,
    pub total_size: u32,
    pub sequence: u32,
    pub fighter_count: u32,

    pub frame: u64,
    pub remaining_frames: u32,
    pub stage_id: i32,
    pub flags: u32,
    pub reserved: u32,

    pub fighters: [FighterState; MAX_FIGHTERS],

    // Protocol v2 host/guest control. These four fields start at byte 288.
    pub gate_enabled: u32,
    pub step_budget: u32,
    pub gate_epoch: u32,
    pub gate_waiting: u32,
}

impl SharedState {
    pub const fn new() -> Self {
        Self {
            magic: *b"SSAI0001",
            protocol_version: PROTOCOL_VERSION,
            total_size: core::mem::size_of::<SharedState>() as u32,
            sequence: 0,
            fighter_count: 0,
            frame: 0,
            remaining_frames: 0,
            stage_id: -1,
            flags: 0,
            reserved: 0,
            fighters: [FighterState::EMPTY; MAX_FIGHTERS],
            gate_enabled: CONTROL_DISABLED,
            step_budget: 0,
            gate_epoch: 0,
            gate_waiting: 0,
        }
    }
}

#[no_mangle]
#[used]
pub static mut SMASH_AI_SHARED_STATE: SharedState = SharedState::new();

static mut FIGHTER_MANAGER_ADDR: usize = 0;
static mut ORIGINAL_ONCE_PER_GAME_FRAME: *mut core::ffi::c_void = core::ptr::null_mut();

type OncePerGameFrameFn = unsafe extern "C" fn(u64);

#[inline]
unsafe fn begin_write() {
    SMASH_AI_SHARED_STATE.sequence = SMASH_AI_SHARED_STATE.sequence.wrapping_add(1) | 1;
    core::sync::atomic::compiler_fence(core::sync::atomic::Ordering::Release);
}

#[inline]
unsafe fn end_write() {
    core::sync::atomic::compiler_fence(core::sync::atomic::Ordering::Release);
    SMASH_AI_SHARED_STATE.sequence = SMASH_AI_SHARED_STATE.sequence.wrapping_add(1) & !1;
}

#[inline]
unsafe fn read_control_u32(ptr: *const u32) -> u32 {
    core::ptr::read_volatile(ptr)
}

#[inline]
unsafe fn write_control_u32(ptr: *mut u32, value: u32) {
    core::ptr::write_volatile(ptr, value);
}

unsafe fn find_text_pattern(pattern: &[u8]) -> Option<usize> {
    let text_start = hooks::getRegionAddress(Region::Text) as usize;
    let rodata_start = hooks::getRegionAddress(Region::Rodata) as usize;

    if text_start == 0 || rodata_start <= text_start || pattern.is_empty() {
        return None;
    }

    let text = core::slice::from_raw_parts(
        text_start as *const u8,
        rodata_start.saturating_sub(text_start),
    );

    text.windows(pattern.len())
        .position(|window| window == pattern)
        .map(|offset| text_start + offset)
}

unsafe fn get_fighter_manager() -> *mut app::FighterManager {
    if FIGHTER_MANAGER_ADDR == 0 {
        return core::ptr::null_mut();
    }

    *(FIGHTER_MANAGER_ADDR as *mut *mut app::FighterManager)
}

unsafe fn sample_fighter(
    mgr: *mut app::FighterManager,
    entry_id: usize,
    frame: u64,
) -> FighterState {
    if mgr.is_null() {
        return FighterState::EMPTY;
    }

    let fighter_entry =
        FighterManager::get_fighter_entry(mgr, app::FighterEntryID(entry_id as i32))
            as *mut app::FighterEntry;

    if fighter_entry.is_null() {
        return FighterState::EMPTY;
    }

    let fighter_id = FighterEntry::current_fighter_id(fighter_entry);
    let module_accessor = app::sv_battle_object::module_accessor(fighter_id as u32);

    if module_accessor.is_null() {
        return FighterState::EMPTY;
    }

    let fighter_kind = app::utility::get_kind(&mut *module_accessor);
    let status_kind = StatusModule::status_kind(module_accessor);
    let situation_kind = StatusModule::situation_kind(module_accessor);

    // FighterManager can expose placeholder entries in Training Mode. They may
    // have a non-null accessor but report kind/status -1. Do not publish those
    // as real P1/P2/P3 fighters.
    if fighter_kind < 0 || status_kind < 0 {
        return FighterState::EMPTY;
    }

    let info =
        FighterManager::get_fighter_information(mgr, app::FighterEntryID(entry_id as i32));

    let (stocks, is_cpu) = if info.is_null() {
        (0, false)
    } else {
        (
            FighterInformation::stock_count(info) as i32,
            FighterInformation::is_operation_cpu(info),
        )
    };

    FighterState {
        sample_frame: frame,
        present: 1,
        fighter_kind,
        status_kind,
        situation_kind,
        stocks,
        is_cpu: if is_cpu { 1 } else { 0 },

        x: PostureModule::pos_x(module_accessor),
        y: PostureModule::pos_y(module_accessor),
        speed_x: KineticModule::get_sum_speed_x(
            module_accessor,
            *KINETIC_ENERGY_RESERVE_ATTRIBUTE_MAIN,
        ),
        speed_y: KineticModule::get_sum_speed_y(
            module_accessor,
            *KINETIC_ENERGY_RESERVE_ATTRIBUTE_MAIN,
        ),
        facing: PostureModule::lr(module_accessor),
        percent: DamageModule::damage(module_accessor, 0),

        motion_kind: MotionModule::motion_kind(module_accessor),
        motion_frame: MotionModule::frame(module_accessor),
        motion_end_frame: MotionModule::end_frame(module_accessor),

        jumps_used: WorkModule::get_int(
            module_accessor,
            *FIGHTER_INSTANCE_WORK_ID_INT_JUMP_COUNT,
        ),
        jumps_max: WorkModule::get_int(
            module_accessor,
            *FIGHTER_INSTANCE_WORK_ID_INT_JUMP_COUNT_MAX,
        ),
    }
}

unsafe fn sample_state() -> bool {
    begin_write();

    let next_frame = SMASH_AI_SHARED_STATE.frame.wrapping_add(1);
    SMASH_AI_SHARED_STATE.frame = next_frame;
    SMASH_AI_SHARED_STATE.fighter_count = 0;
    SMASH_AI_SHARED_STATE.remaining_frames = 0;
    SMASH_AI_SHARED_STATE.stage_id = -1;
    SMASH_AI_SHARED_STATE.flags = 0;
    SMASH_AI_SHARED_STATE.fighters = [FighterState::EMPTY; MAX_FIGHTERS];

    let mut in_match = false;
    let mgr = get_fighter_manager();
    if !mgr.is_null() {
        let entry_count_raw = FighterManager::entry_count(mgr);
        let entry_count = if entry_count_raw > 0 {
            core::cmp::min(entry_count_raw as usize, MAX_FIGHTERS)
        } else {
            0
        };

        in_match = entry_count > 0 && !FighterManager::is_result_mode(mgr);
        if in_match {
            SMASH_AI_SHARED_STATE.flags |= FLAG_IN_MATCH;
            SMASH_AI_SHARED_STATE.remaining_frames = get_remaining_time_as_frame();
            SMASH_AI_SHARED_STATE.stage_id = stage_id();
        }

        let mut present_count = 0u32;

        for entry_id in 0..entry_count {
            let fighter = sample_fighter(mgr, entry_id, next_frame);

            if fighter.present != 0 {
                present_count += 1;
                if fighter.status_kind == *FIGHTER_STATUS_KIND_DEAD {
                    SMASH_AI_SHARED_STATE.flags |= 1 << (FLAG_DEAD_BASE + entry_id as u32);
                }
                if fighter.status_kind == *FIGHTER_STATUS_KIND_REBIRTH {
                    SMASH_AI_SHARED_STATE.flags |= 1 << (FLAG_REBIRTH_BASE + entry_id as u32);
                }
                if fighter.status_kind == *FIGHTER_STATUS_KIND_ENTRY {
                    SMASH_AI_SHARED_STATE.flags |= 1 << (FLAG_ENTRY_BASE + entry_id as u32);
                }
            }

            SMASH_AI_SHARED_STATE.fighters[entry_id] = fighter;
        }

        SMASH_AI_SHARED_STATE.fighter_count = present_count;
    }

    end_write();
    in_match
}

unsafe fn gate_at_frame_boundary(in_match: bool) {
    let enabled_ptr = core::ptr::addr_of!(SMASH_AI_SHARED_STATE.gate_enabled);
    let enabled_mut = core::ptr::addr_of_mut!(SMASH_AI_SHARED_STATE.gate_enabled);
    let budget_ptr = core::ptr::addr_of!(SMASH_AI_SHARED_STATE.step_budget);
    let budget_mut = core::ptr::addr_of_mut!(SMASH_AI_SHARED_STATE.step_budget);
    let epoch_ptr = core::ptr::addr_of!(SMASH_AI_SHARED_STATE.gate_epoch);
    let epoch_mut = core::ptr::addr_of_mut!(SMASH_AI_SHARED_STATE.gate_epoch);
    let waiting_mut = core::ptr::addr_of_mut!(SMASH_AI_SHARED_STATE.gate_waiting);

    if !in_match || read_control_u32(enabled_ptr) != CONTROL_ENABLED {
        write_control_u32(waiting_mut, 0);
        return;
    }

    let budget = read_control_u32(budget_ptr);
    if budget > 0 {
        let remaining = budget - 1;
        write_control_u32(budget_mut, remaining);

        if remaining > 0 {
            write_control_u32(waiting_mut, 0);
            return;
        }
    }

    // We are at a stable SSBU frame boundary. Keep the game-frame thread here
    // until the host supplies a positive step budget or disables the gate.
    write_control_u32(waiting_mut, 1);
    write_control_u32(
        epoch_mut,
        read_control_u32(epoch_ptr).wrapping_add(1),
    );

    while read_control_u32(enabled_ptr) == CONTROL_ENABLED
        && read_control_u32(budget_ptr) == 0
    {
        // Sleeping this one guest thread avoids burning a host core while still
        // leaving the exporter memory readable from Ryujinx's host bridge.
        std::thread::sleep(Duration::from_micros(100));
    }

    write_control_u32(waiting_mut, 0);
}

unsafe extern "C" fn once_per_game_frame_hook(game_state_ptr: u64) {
    if !ORIGINAL_ONCE_PER_GAME_FRAME.is_null() {
        let original: OncePerGameFrameFn =
            core::mem::transmute(ORIGINAL_ONCE_PER_GAME_FRAME);
        original(game_state_ptr);
    }

    let in_match = sample_state();
    gate_at_frame_boundary(in_match);
}

#[skyline::main(name = "smash_ai_state")]
pub fn main() {
    unsafe {
        let result = skyline::nn::ro::LookupSymbol(
            &mut FIGHTER_MANAGER_ADDR,
            "_ZN3lib9SingletonIN3app14FighterManagerEE9instance_E\0"
                .as_bytes()
                .as_ptr(),
        );

        if result != 0 || FIGHTER_MANAGER_ADDR == 0 {
            skyline::println!(
                "[smash_ai_state] FighterManager lookup failed: result={:#x}, addr={:#x}",
                result,
                FIGHTER_MANAGER_ADDR
            );
            return;
        }

        let Some(target) = find_text_pattern(ONCE_PER_GAME_FRAME_PATTERN) else {
            skyline::println!(
                "[smash_ai_state] once-per-game-frame signature not found; exporter disabled"
            );
            return;
        };

        hooks::A64HookFunction(
            target as *const core::ffi::c_void,
            once_per_game_frame_hook as *const () as *const core::ffi::c_void,
            &mut ORIGINAL_ONCE_PER_GAME_FRAME,
        );

        skyline::println!(
            "[smash_ai_state] protocol v2 direct frame hook installed at {:#x}; exact-step gate available",
            target
        );
    }
}
