use smash::app::{self, lua_bind::*};
use smash::lib::lua_const::*;
use smash::lua2cpp::L2CFighterCommon;
use smashline::{Agent, Main};

const MAX_FIGHTERS: usize = 3;
const PROTOCOL_VERSION: u32 = 1;

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
        }
    }
}

#[no_mangle]
#[used]
pub static mut SMASH_AI_SHARED_STATE: SharedState = SharedState::new();

static mut FIGHTER_MANAGER_ADDR: usize = 0;

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

unsafe extern "C" fn fighter_frame(fighter: &mut L2CFighterCommon) {
    let module_accessor = fighter.module_accessor;
    if module_accessor.is_null() {
        return;
    }

    let entry_id =
        WorkModule::get_int(module_accessor, *FIGHTER_INSTANCE_WORK_ID_INT_ENTRY_ID) as usize;

    if entry_id >= MAX_FIGHTERS {
        return;
    }

    begin_write();

    if entry_id == 0 {
        SMASH_AI_SHARED_STATE.frame = SMASH_AI_SHARED_STATE.frame.wrapping_add(1);
        SMASH_AI_SHARED_STATE.remaining_frames = get_remaining_time_as_frame();
        SMASH_AI_SHARED_STATE.stage_id = stage_id();

        if FIGHTER_MANAGER_ADDR != 0 {
            let mgr = *(FIGHTER_MANAGER_ADDR as *mut *mut app::FighterManager);
            if !mgr.is_null() {
                let entry_count = FighterManager::entry_count(mgr);
                let in_match = entry_count > 0 && !FighterManager::is_result_mode(mgr);
                SMASH_AI_SHARED_STATE.flags = if in_match { 1 } else { 0 };
                SMASH_AI_SHARED_STATE.fighter_count =
                    core::cmp::min(entry_count as usize, MAX_FIGHTERS) as u32;
            }
        }
    }

    let mut stocks = 0;
    let mut is_cpu = false;

    if FIGHTER_MANAGER_ADDR != 0 {
        let mgr = *(FIGHTER_MANAGER_ADDR as *mut *mut app::FighterManager);
        if !mgr.is_null() {
            let info = FighterManager::get_fighter_information(mgr, app::FighterEntryID(entry_id as i32));
            if !info.is_null() {
                stocks = FighterInformation::stock_count(info);
                is_cpu = FighterInformation::is_operation_cpu(info);
            }
        }
    }

    let state = &mut SMASH_AI_SHARED_STATE.fighters[entry_id];
    state.sample_frame = SMASH_AI_SHARED_STATE.frame;
    state.present = 1;
    state.fighter_kind = app::utility::get_kind(&mut *module_accessor);
    state.status_kind = StatusModule::status_kind(module_accessor);
    state.situation_kind = StatusModule::situation_kind(module_accessor);
    state.stocks = stocks as i32;
    state.is_cpu = if is_cpu { 1 } else { 0 };

    state.x = PostureModule::pos_x(module_accessor);
    state.y = PostureModule::pos_y(module_accessor);
    state.speed_x =
        KineticModule::get_sum_speed_x(module_accessor, *KINETIC_ENERGY_RESERVE_ATTRIBUTE_MAIN);
    state.speed_y =
        KineticModule::get_sum_speed_y(module_accessor, *KINETIC_ENERGY_RESERVE_ATTRIBUTE_MAIN);
    state.facing = PostureModule::lr(module_accessor);
    state.percent = DamageModule::damage(module_accessor, 0);

    state.motion_kind = MotionModule::motion_kind(module_accessor);
    state.motion_frame = MotionModule::frame(module_accessor);
    state.motion_end_frame = MotionModule::end_frame(module_accessor);

    state.jumps_used =
        WorkModule::get_int(module_accessor, *FIGHTER_INSTANCE_WORK_ID_INT_JUMP_COUNT);
    state.jumps_max =
        WorkModule::get_int(module_accessor, *FIGHTER_INSTANCE_WORK_ID_INT_JUMP_COUNT_MAX);

    if SMASH_AI_SHARED_STATE.fighter_count < (entry_id + 1) as u32 {
        SMASH_AI_SHARED_STATE.fighter_count = (entry_id + 1) as u32;
    }

    end_write();
}

#[skyline::main(name = "smash_ai_state")]
pub fn main() {
    unsafe {
        skyline::nn::ro::LookupSymbol(
            &mut FIGHTER_MANAGER_ADDR,
            "_ZN3lib9SingletonIN3app14FighterManagerEE9instance_E\0"
                .as_bytes()
                .as_ptr(),
        );
    }

    Agent::new("fighter").on_line(Main, fighter_frame).install();
}
