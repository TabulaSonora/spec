# Symbol map — `SCCore.dll` recovered function names

Function and data-label names recovered while reverse-engineering the Sound Canvas VA
tone-generator core. These names are applied to the Ghidra database
(`tools/ghidra_scripts/Rename*.java`) and flow into `SCCore.decompiled.c` on regeneration.

Addresses are virtual (image base `0x180000000`). `.text` file offset = VA − 0x180000000 − 0xC00.
Names are best-effort from dataflow/caller analysis; treat single-purpose leaf names as high
confidence and large dispatch/effect routines as structurally-certain-but-approximate.

Coverage: **749 named functions** of 1045. The unnamed remainder is low-value one-byte SysEx
field writers plus the table-dispatched functions recovered by `DefineTableFunctions.java` that
have not been analysed yet — those decompile correctly, they simply carry `FUN_` names.

## Contents
- [`00000`–`04000` — Voice / partial allocator + voice stealer](#voice--partial-allocator--voice-stealer)
- [`04000`–`10000` — Effects DSP graph + reverb/chorus control](#effects-dsp-graph--reverbchorus-control)
- [`10000`–`40000` — EFX algorithm bank](#efx-algorithm-bank)
- [`40000`–`5c000` — EFX parameter apply + voice/part/tone pools](#efx-parameter-apply--voiceparttone-pools)
- [`5c000`–`60000` — Part/tone assignment + voice init](#parttone-assignment--voice-init)
- [`60000`–`68000` — Synth voice core + MIDI stream/SysEx/mod-matrix](#synth-voice-core--midi-streamsysexmodmatrix)
- [`68000`–`70000` — Note engine + GS SysEx DT1/RQ1 state machine](#note-engine--gs-sysex-dt1rq1-state-machine)
- [`70000`–`78000` — GS SysEx part/system/effect parameter handlers](#gs-sysex-partsystemeffect-parameter-handlers)
- [`78000`–`80000` — GS SysEx bulk dump + reverb/chorus DSP config](#gs-sysex-bulk-dump--reverbchorus-dsp-config)
- [`80000`–`90000` — MIDI ingest + per-tick modulation + audio render + display](#midi-ingest--pertick-modulation--audio-render--display)
- [`90000`–`a0000` — CRT / host runtime tail](#crt--host-runtime-tail)

## Voice / partial allocator + voice stealer

`0x180000000`–`0x180004000`. Polyphony: note-group and partial-voice freelists, LRU priority list, voice stealing, poly/mono note assignment.

| Address | Name | Purpose |
|---------|------|---------|
| `180001040` | `note_lru_unlink` | unlink note-group from global LRU list |
| `1800010a0` | `part_voice_desc_reset` | swap-in fresh voice-build descriptor node |
| `180001130` | `partial_shared_node_alloc` | pop shared env/mod node from freelist |
| `180001180` | `note_group_lru_push` | init+push note-group onto LRU/priority list |
| `180001230` | `partial_voice_free` | free partial voice; unlink chain |
| `1800013b0` | `partial_shared_node_free` | return shared env/mod node to freelist |
| `180001450` | `note_group_free` | unlink note-group from part list + LRU |
| `1800014c0` | `note_group_alloc` | alloc note-group+partial chain, steal if full |
| `1800017e0` | `note_group_realloc` | grow/shrink partial count of existing group |
| `180001c50` | `partial_release` | quiet + fast-release one partial voice |
| `180001cf0` | `part_notes_note_off` | mark held note-groups for release |
| `180001d80` | `part_sustain_release` | clear sustain-hold flags; release held groups |
| `180001de0` | `part_voices_flush` | force-free all note-groups on a part |
| `180001ea0` | `voice_steal_run` | reclaim voices across parts when over polyphony |
| `180002150` | `note_group_steal` | free all partials of one note-group |
| `1800021e0` | `voice_steal_one` | steal single lowest-age partial in part |
| `1800022d0` | `voice_steal_oldest` | steal oldest partial, pair-aware |
| `1800023f0` | `voice_steal_pair_free` | free a partial pair |
| `1800024d0` | `voice_steal_until` | retry per-part steal until success |
| `180002530` | `partial_level_setup` | per-partial level via keyzone + expression |
| `1800026d0` | `tone_lookup` |  |
| `180002960` | `tone_lookup_direct` | tone lookup from direct tone ptr |
| `1800029e0` | `partial_alloc_node` |  |
| `180002f30` | `partial_build` |  |
| `1800031f0` | `multisample_key_zone` |  |
| `180003420` | `multisample_select_wave` |  |
| `1800035f0` | `note_assign_poly` | poly note-on: build/steal partials, alloc nodes |
| `1800038b0` | `note_assign_mono` | mono/legato note-on assign |
| `180003a60` | `tone_apply_velocity_splits` |  |
| `180003c80` | `tone_resolve` |  |
| `180003e90` | `partial_velocity_gate` |  |

## Effects DSP graph + reverb/chorus control

`0x180004000`–`0x180010000`. The ported MB87837 effects DSP: chorus/reverb state machines, coefficient programming, and the single-op float graph primitives (dsp_*).

| Address | Name | Purpose |
|---------|------|---------|
| `180004000` | `fx_stage_noop` | fx state handler returning 0 |
| `180004010` | `chorus_state_reset` | chorus fx state reset |
| `180004090` | `chorus_apply_params` | write chorus rate/depth/fb |
| `180004190` | `chorus_apply_levels` | set chorus send levels |
| `1800041f0` | `chorus_update` | chorus combined param+level update |
| `180004290` | `fx_stage_ret1` | fx state handler returning 1 |
| `1800042c0` | `fx_task_schedule` | arm deferred fx task callback |
| `180004300` | `fx_task_init` | init/start one of 3 fx timer tasks |
| `180004390` | `fx_task_callback` | fx timer trampoline: reschedule + invoke |
| `1800043c0` | `reverb_state_reset` | reverb fx state reset |
| `180004410` | `reverb_init_taps` | load reverb delay-tap register addresses |
| `1800044d0` | `reverb_apply_params` | write reverb time/level/HF-damp regs |
| `1800046e0` | `reverb_apply_levels` | set reverb send levels |
| `180004730` | `reverb_update` | reverb combined param+level update |
| `180004840` | `fx_control_update` | master fx state-machine update loop |
| `1800051e0` | `fx_coef_table_select` | select fx coefficient table by index |
| `1800052d0` | `_guard_check_icall` |  |
| `1800052e0` | `reverb_calc_coef` | compute reverb/delay coef from time params |
| `1800053e0` | `reverb_load_algo_regs` | program reverb/delay register bank |
| `180005860` | `chorus_load_algo_regs` | program chorus register bank |
| `180005ae0` | `fx_reg_write_pair_ordered` | write two slewed regs in sorted order |
| `180005b60` | `dsp_store` | store value into graph node |
| `180005b70` | `dsp_negate` | float sign-negate |
| `180005b90` | `dsp_add` | float a+b |
| `180005bb0` | `dsp_mul` | float a*b |
| `180005bd0` | `dsp_acc` | float in-place add |
| `180005be0` | `dsp_abs` | float fabs |
| `180005c20` | `dsp_phase_wrap` | wrap float to [-1,1) |
| `180005c80` | `dsp_wavetable_lookup` | table lookup w/ interp |
| `180005d10` | `dsp_add_eps` | add denormal epsilon |
| `180005d30` | `fx_algo_2_voice_pitch_shifter` |  |
| `180006bb0` | `dsp_is_nonneg` | predicate x>=0 |
| `180006bc0` | `dsp_is_neg` | predicate x<0 |
| `180006bd0` | `dsp_clamp_unit` | clamp float to [-1,1] |
| `180006c10` | `fx_algo_3d_manual` |  |
| `1800090f0` | `fx_algo_3d_auto` |  |
| `18000b5d0` | `fx_algo_3d_chorus` |  |
| `18000c150` | `fx_algo_3d_delay` |  |
| `18000cb00` | `fx_stereo_balance_clamp` | clamp pair to [0,1.95], sum<=2 |
| `18000cbe0` | `fx_algo_auto_wah` |  |
| `18000d5f0` | `fx_algo_bass_multi` |  |
| `18000e860` | `fx_algo_chorus_to_delay` |  |
| `18000f340` | `fx_algo_stereo_chorus` |  |
| `18000fd90` | `fx_algo_chorus_par_delay` |  |

## EFX algorithm bank

`0x180010000`–`0x180040000`. The insertion-effect (EFX) algorithms (fx_algo_*) and their crossfade/transition state machine.

| Address | Name | Purpose |
|---------|------|---------|
| `180010870` | `fx_algo_chorus_par_flanger` |  |
| `1800116a0` | `fx_algo_c_guitar_multi_1` |  |
| `1800123c0` | `fx_algo_c_guitar_multi_2` |  |
| `180013d30` | `fx_algo_chorus_to_flanger` |  |
| `180014b40` | `fx_algo_compressor` |  |
| `180015520` | `fx_algo_distortion_to_chorus` |  |
| `1800164a0` | `fx_algo_distortion_to_delay` |  |
| `1800170f0` | `fx_algo_distortion_to_flanger` |  |
| `180018070` | `fx_algo_none_placeholder` |  |
| `180018560` | `fx_algo_distortion` |  |
| `180018f50` | `fx_algo_enhancer_to_chorus` |  |
| `180019be0` | `fx_algo_enhancer_to_delay` |  |
| `18001a450` | `fx_algo_enhancer_to_flanger` |  |
| `18001b0e0` | `fx_algo_enhancer` |  |
| `18001b730` | `fx_algo_stereo_eq` |  |
| `18001be60` | `fx_algo_flanger_to_delay` |  |
| `18001c940` | `fx_algo_stereo_flanger` |  |
| `18001d390` | `fx_algo_flanger_par_delay` |  |
| `18001de70` | `fx_algo_feedback_pitch_shifter` |  |
| `18001e7c0` | `fx_algo_gate_reverb` |  |
| `18001f2c0` | `fx_algo_guitar_multi_1` |  |
| `1800205c0` | `fx_algo_guitar_multi_2` |  |
| `180021820` | `fx_algo_guitar_multi_3` |  |
| `180022b80` | `fx_algo_hexa_chorus` |  |
| `1800238f0` | `fx_algo_humanizer` |  |
| `180024420` | `fx_algo_keyboard_multi` |  |
| `1800259f0` | `fx_algo_limiter` |  |
| `180026560` | `fx_algo_lo_fi_1` |  |
| `180027800` | `fx_algo_lo_fi_2` |  |
| `180029c90` | `fx_algo_orphan66_moddelay` |  |
| `18002a540` | `fx_algo_modulation_delay` |  |
| `18002af50` | `fx_algo_overdrive_to_chorus` |  |
| `18002bed0` | `fx_algo_overdrive_to_delay` |  |
| `18002cb20` | `fx_algo_overdrive_to_flanger` |  |
| `18002daa0` | `fx_algo_overdrive` |  |
| `18002e490` | `fx_algo_overdrive_par_auto_wah` |  |
| `18002f450` | `fx_algo_overdrive_1_par_overdrive_2` |  |
| `180030460` | `fx_algo_overdrive_par_phaser` |  |
| `1800312c0` | `fx_algo_overdrive_par_rotary` |  |
| `180032700` | `fx_algo_auto_pan` |  |
| `180032ee0` | `fx_algo_phaser_par_auto_wah` |  |
| `180033d30` | `fx_algo_phaser_par_rotary` |  |
| `180034f30` | `fx_algo_phaser` |  |
| `180035b70` | `fx_algo_quadruple_tap_delay` |  |
| `180036230` | `fx_algo_reverb` |  |
| `180036d70` | `fx_algo_ep_multi` |  |
| `1800382f0` | `fx_algo_rotary` |  |
| `1800390a0` | `fx_algo_rotary_multi` |  |
| `18003a840` | `fx_algo_space_d` |  |
| `18003b150` | `fx_algo_spectrum` |  |
| `18003ba70` | `fx_algo_step_flanger` |  |
| `18003c510` | `fx_algo_stereo_delay` |  |
| `18003cba0` | `fx_algo_time_controllable_delay` |  |
| `18003d220` | `fx_algo_thru` |  |
| `18003d440` | `fx_algo_tremolo_chorus` |  |
| `18003e2d0` | `fx_algo_triple_tap_delay` |  |
| `18003e8a0` | `fx_algo_tremolo` |  |
| `18003f080` | `fx_transition_mute_wet` | zero EFX wet-mix regs, arm crossfade counter=2 |
| `18003f100` | `fx_transition_stage_bypass` | select algo index 1, set xfade counter=4 |
| `18003f140` | `fx_select_algo_from_type` |  |
| `18003f220` | `fx_transition_commit_params` | countdown; on 0 run param-proc mode 2, restore wet 0x7f |
| `18003f360` | `fx_transition_step_params` | per-block EFX param-interp step (param-proc mode 3) |
| `18003f450` | `fx_get_default_params_for_type` | map GS EFX type->algo, copy 0x1c-byte default param block |
| `18003f4e0` | `dpcm_voice_init_fwd` |  |
| `18003f720` | `voice_render_dispatch` |  |
| `18003f870` | `sample_fetch_loop_wrap` |  |
| `18003f920` | `sample_fetch_loop_wrap_reverse` | reverse-playback counterpart of sample_fetch_loop_wrap |
| `18003f9d0` | `sampler_pcm` |  |
| `18003fb80` | `sampler_adpcm4` |  |
| `18003fdd0` | `sampler_fmt4` |  |
| `18003ff90` | `dpcm_voice_init_rev` |  |

## EFX parameter apply + voice/part/tone pools

`0x180040000`–`0x18005c000`. Per-algorithm EFX parameter application (fx_param_apply_*), slew helpers, and voice/part/tone pool allocation + defaults.

| Address | Name | Purpose |
|---------|------|---------|
| `180040210` | `sampler_pcm_alt` |  |
| `1800403c0` | `sampler_adpcm4_alt` |  |
| `180040610` | `sampler_fmt4_alt` |  |
| `1800407d0` | `fx_eq_band_preset_apply` | indexed stereo EQ/band coeff apply |
| `180040a40` | `fx_param_apply_40a40` | EFX per-algorithm parameter apply handler |
| `180040d70` | `fx_param_apply_40d70` | EFX per-algorithm parameter apply handler |
| `180041340` | `fx_param_apply_41340` | EFX per-algorithm parameter apply handler |
| `180041a50` | `fx_param_apply_41a50` | EFX per-algorithm parameter apply handler |
| `180041c90` | `fx_param_apply_41c90` | EFX per-algorithm parameter apply handler |
| `1800421f0` | `fx_param_apply_421f0` | EFX per-algorithm parameter apply handler |
| `180043270` | `fx_param_apply_43270` | EFX per-algorithm parameter apply handler |
| `180043a60` | `fx_param_apply_43a60` | EFX per-algorithm parameter apply handler |
| `1800443f0` | `fx_param_apply_443f0` | EFX per-algorithm parameter apply handler |
| `1800449b0` | `fx_param_apply_449b0` | EFX per-algorithm parameter apply handler |
| `180044fe0` | `fx_param_apply_44fe0` | EFX per-algorithm parameter apply handler |
| `180045450` | `fx_param_apply_45450` | EFX per-algorithm parameter apply handler |
| `1800459b0` | `fx_param_apply_459b0` | EFX per-algorithm parameter apply handler |
| `180046070` | `fx_param_apply_46070` | EFX per-algorithm parameter apply handler |
| `180046640` | `fx_param_apply_46640` | EFX per-algorithm parameter apply handler |
| `180046980` | `fx_param_apply_46980` | EFX per-algorithm parameter apply handler |
| `180046e20` | `fx_param_apply_46e20` | EFX per-algorithm parameter apply handler |
| `180047340` | `fx_param_apply_47340` | EFX per-algorithm parameter apply handler |
| `180047710` | `fx_param_apply_47710` | EFX per-algorithm parameter apply handler |
| `180047c10` | `fx_param_apply_47c10` | EFX per-algorithm parameter apply handler |
| `1800481c0` | `fx_param_apply_481c0` | EFX per-algorithm parameter apply handler |
| `180048680` | `fx_param_apply_48680` | EFX per-algorithm parameter apply handler |
| `180048b40` | `fx_param_apply_48b40` | EFX per-algorithm parameter apply handler |
| `180049080` | `fx_param_apply_49080` | EFX per-algorithm parameter apply handler |
| `180049e40` | `fx_param_apply_49e40` | EFX per-algorithm parameter apply handler |
| `18004ab60` | `fx_param_apply_4ab60` | EFX per-algorithm parameter apply handler |
| `18004b980` | `fx_param_apply_4b980` | EFX per-algorithm parameter apply handler |
| `18004c210` | `fx_param_apply_4c210` | EFX per-algorithm parameter apply handler |
| `18004c830` | `fx_param_apply_4c830` | EFX per-algorithm parameter apply handler |
| `18004d0f0` | `fx_param_apply_4d0f0` | EFX per-algorithm parameter apply handler |
| `18004daa0` | `fx_param_apply_4daa0` | EFX per-algorithm parameter apply handler |
| `18004e860` | `fx_param_apply_4e860` | EFX per-algorithm parameter apply handler |
| `18004ee40` | `fx_slew_reset_4ee40` | zero 4 slew regs (0xee/0x125/0xb2/0xc7) |
| `18004ee80` | `fx_slew_restore_4ee80` | restore slew regs from param[2] |
| `18004efe0` | `fx_param_apply_4efe0` | EFX per-algorithm parameter apply handler |
| `18004f560` | `fx_param_apply_4f560` | EFX per-algorithm parameter apply handler |
| `18004fb00` | `fx_param_apply_4fb00` | EFX per-algorithm parameter apply handler |
| `180050080` | `fx_param_apply_50080` | EFX per-algorithm parameter apply handler |
| `180050620` | `fx_slew_restore_50620` | restore slew regs from param[8] |
| `1800506e0` | `fx_param_apply_506e0` | EFX per-algorithm parameter apply handler |
| `180050c20` | `fx_param_apply_50c20` | EFX per-algorithm parameter apply handler |
| `1800511a0` | `fx_param_apply_511a0` | EFX per-algorithm parameter apply handler |
| `180051be0` | `fx_param_apply_51be0` | EFX per-algorithm parameter apply handler |
| `180052820` | `fx_param_apply_52820` | EFX per-algorithm parameter apply handler |
| `180053070` | `fx_param_apply_53070` | EFX per-algorithm parameter apply handler |
| `180053a10` | `fx_param_apply_53a10` | EFX per-algorithm parameter apply handler |
| `180053d90` | `fx_param_apply_53d90` | EFX per-algorithm parameter apply handler |
| `1800541c0` | `fx_param_apply_541c0` | EFX per-algorithm parameter apply handler |
| `1800547e0` | `fx_param_apply_547e0` | EFX per-algorithm parameter apply handler |
| `180054d40` | `fx_param_apply_54d40` | EFX per-algorithm parameter apply handler |
| `1800556e0` | `fx_param_apply_556e0` | EFX per-algorithm parameter apply handler |
| `180055e90` | `fx_param_apply` |  |
| `180056560` | `fx_program_load` |  |
| `180056d30` | `fx_param_apply_56d30` | EFX per-algorithm parameter apply handler |
| `1800575f0` | `fx_slew_restore_575f0` | restore 3 slew regs from param[0xc]/[0xd] |
| `180057690` | `fx_param_apply_57690` | EFX per-algorithm parameter apply handler |
| `180057bc0` | `fx_param_apply_57bc0` | EFX per-algorithm parameter apply handler |
| `1800580c0` | `fx_param_apply_580c0` | EFX per-algorithm parameter apply handler |
| `1800585e0` | `fx_param_apply_585e0` | EFX per-algorithm parameter apply handler |
| `180058b90` | `fx_apply_reg_group_58b90` | write reg group from param block + reg-index array |
| `180058f40` | `fx_param_apply_58f40` | EFX per-algorithm parameter apply handler |
| `180059630` | `fx_param_apply_59630` | EFX per-algorithm parameter apply handler |
| `180059d80` | `fx_slew_reset_59d80` | zero 4 slew regs (0xe7/0x12a/0xe4/0x127) |
| `180059dc0` | `fx_slew_restore_59dc0` | restore slew regs from level index |
| `180059e50` | `fx_param_apply_59e50` | EFX per-algorithm parameter apply handler |
| `18005a3d0` | `fx_slew_reset_5a3d0` | zero 6 slew regs (0xb4/0xde/0x8a..0x8e) |
| `18005a420` | `fx_slew_restore_5a420` | restore slew regs from param[3]/[0x13] |
| `18005a520` | `fx_param_apply_5a520` | EFX per-algorithm parameter apply handler |
| `18005aa00` | `fx_slew_reset_5aa00` | zero 4 slew regs (0xf2/0x139/0xef/0x136) |
| `18005aa40` | `fx_slew_restore_5aa40` | restore slew regs from param[0xf] |
| `18005aab0` | `fx_param_apply_5aab0` | EFX per-algorithm parameter apply handler |
| `18005ac00` | `fx_param_apply_5ac00` | EFX per-algorithm parameter apply handler |
| `18005b230` | `fx_param_apply_5b230` | EFX per-algorithm parameter apply handler |
| `18005b820` | `fx_param_apply_5b820` | EFX per-algorithm parameter apply handler |
| `18005bc10` | `snd_get_const_58` | const getter returns 0x58 |
| `18005bc60` | `snd_get_const_93b00` | const getter returns 0x93b00 |
| `18005bc70` | `voice_pool_init` | alloc part/voice/tone pools, install accessor fn-ptrs |

## Part/tone assignment + voice init

`0x18005c000`–`0x180060000`. Bank/program to tone resolution, per-note wave/multisample map build, fresh/parent voice init.

| Address | Name | Purpose |
|---------|------|---------|
| `18005c180` | `voice_pool_load_defaults` | memcpy default part+voice state from ROM into pools |
| `18005c2d0` | `voice_get_by_index` | returns &voice_pool[(idx-1)*0x488] |
| `18005c3c0` | `tone_tableA_load_defaults` | memcpy two 0x68c ROM blocks into tableA |
| `18005c420` | `tone_tableB_load_defaults` | memcpy two 0x580 ROM blocks into tableB |
| `18005c5d0` | `voice_calc_peak_load` | sum voice samplerate*length load, clamp >>15 |
| `18005c6a0` | `part_param_get` | switch: read part-struct field by addr/index into buf |
| `18005ccd0` | `part_pool_reset_all` | reinit all parts: assign tone, clear voice ptrs |
| `18005cf30` | `part_collect_bank_prog` | gather each part's bank/prog fields into arrays |
| `18005cf90` | `part_assign_tone` | resolve bank/prog to tone, memcpy tone into part |
| `18005d1d0` | `part_copy_tone_assign` | copy tone-assignment fields part->part |
| `18005d290` | `part_build_note_map` | expand tone to per-note wave/multisample index map |
| `18005d580` | `ramp_env_step_eval` | ramp/env step: interpolate g_ramp_exp_tbl, advance |
| `18005d8d0` | `voice_ctrl_ramp_c` |  |
| `18005dbf0` | `voice_ctrl_ramp_d` |  |
| `18005e040` | `voice_ctrl_ramp_a` |  |
| `18005e990` | `voice_ctrl_ramp_b` |  |
| `18005ec90` | `wavedesc_decode` |  |
| `18005ee30` | `partial_load_params` |  |
| `18005f270` | `voice_pool_clear_flags` | zero two status fields across all 0x40 voice slots |
| `18005f5c0` | `note_on_voice_setup` | note-on: copy param block, init voice pitch/pan/seed |
| `18005f8e0` | `voice_init_from_parent` | init voice pitch/state, inherit from parent voice |
| `18005fab0` | `voice_init_fresh` | fresh voice init: base pitch table + LFSR seed |
| `18005fc20` | `partial_compute_pitch` |  |
| `18005fde0` | `partial_compute_pitch_env` | note-on: 5 absolute env levels -> voice+0x64/0x68/0x210/0x214/0x80 |

## Synth voice core + MIDI stream/SysEx/mod-matrix

`0x180060000`–`0x180068000`. Pitch/TVF/TVA envelope setup, the MIDI byte-stream parser, RPN/NRPN decode, the modulation matrix, and engine reset.

| Address | Name | Purpose |
|---------|------|---------|
| `1800600c0` | `pitch_env_apply_stage` | actually the `block[0x1a]` random start-pitch jitter (see FINDINGS "TVP runtime machine") |
| `180060150` | `partial_apply_pitch_env_rates` | seg0 rate word +0x62; seg1-3 times +0x204/6/8; release rate +0x7a (doubles as enable) |
| `180060390` | `voice_volume_apply` |  |
| `180060560` | `pitch_env_rand_init` | randomize partial-group pitch-env via prng+pitch split tables |
| `180060620` | `tvf_env_prep` |  |
| `1800607e0` | `env_rate_scale` |  |
| `180060880` | `env_level_scale` |  |
| `180060960` | `tva_compute_base_level` |  |
| `180060b00` | `tva_compute_env_levels` |  |
| `180060ca0` | `tva_compute_env_rates` |  |
| `180061030` | `partial_pitch_env_init` | set pitch-env targets + interp/mode from wave/multisample fades |
| `180061210` | `partial_compute_filter` |  |
| `180061640` | `tvf_env_level_conv` |  |
| `1800616f0` | `tvf_compute_env_rates` | TVF env segment rates/levels into voice +0x36..+0x4e |
| `180061a40` | `part_start_voices` |  |
| `180061d00` | `fx_master_level_update` | update reverb/chorus master levels via fx_level_to_blockfloat |
| `180061d40` | `fx_level_to_blockfloat` | encode linear level -> (exponent,fraction) pair |
| `180061e60` | `fx_output_routing_set` | toggle FX routing regs between 0x20 and 0x7f |
| `180061fa0` | `fx_reg_write8` | write low byte of one FX shadow register |
| `180061ff0` | `fx_reg_write8_flag` | write FX reg low byte with 0x8000 enable-flag gate |
| `180062050` | `fx_reg_write16` |  |
| `180062100` | `fx_reg_write_lfo5` | write 5 packed FX LFO regs + trailing reg |
| `1800621f0` | `fx_reg_write_slew` |  |
| `1800622b0` | `fx_reg_write16_slew` | slew a 16-bit FX register high byte toward target |
| `180062410` | `fx_set_algo_index` |  |
| `180062520` | `engine_worker_dispatch` | one-time engine init + drain work flags |
| `180062780` | `sysex_addr_lookup` | build 3-byte SysEx parameter address from table |
| `1800627e0` | `sysex_send_param_response` | assemble+enqueue SysEx param dump (RQ1/DT1) |
| `180062b20` | `bank_program_scan` | scan 16 parts bank/program, resolve maps, emit SysEx |
| `180062d70` | `midi_stream_parse` | MIDI byte-stream state machine -> structured events |
| `1800630f0` | `rpn_nrpn_dispatch_guard` | call rpn_nrpn_decode, null for suppressed params |
| `180063120` | `sysex_bulk_capture` | match SysEx identity/dev-id/checksum, malloc+copy dump |
| `1800632c0` | `sysex_bulk_capture2` | early-return variant of sysex_bulk_capture |
| `180063470` | `sysex_buf_append` | append bytes to SysEx out buffer, terminate 0xF7 |
| `1800634c0` | `midi_event_classify` | expand MIDI msg (running/realtime) -> event type |
| `180063950` | `drum_channel_map_init` | fill 16-ch note-marker table (ch%16==9) |
| `180063e30` | `drum_channel_flag_init` | fill 16-ch GM drum-channel flag table |
| `1800643a0` | `rpn_nrpn_decode` | switch decoding RPN/NRPN parameter address+data |
| `180064b20` | `caseD_63` |  |
| `180064b90` | `caseD_42` |  |
| `180064bd0` | `caseD_58` |  |
| `180064c10` | `caseD_16` |  |
| `180064c50` | `caseD_79` |  |
| `180064ca0` | `caseD_3f` |  |
| `180064cf0` | `caseD_4a` |  |
| `180064d30` | `caseD_34` |  |
| `180064d70` | `caseD_3e` |  |
| `180064de0` | `caseD_49` |  |
| `180064e50` | `caseD_33` |  |
| `180064ec0` | `caseD_38` |  |
| `180064f30` | `caseD_2d` |  |
| `180064fa0` | `caseD_6f` |  |
| `180065010` | `caseD_76` |  |
| `180065060` | `caseD_7a` |  |
| `1800650c0` | `caseD_81` |  |
| `180065110` | `caseD_12` |  |
| `180065170` | `midi_input_service` | MIDI input service loop over event ring (default) |
| `180065340` | `midi_input_service_mode4` | MIDI input service loop (mode4 callbacks) |
| `1800653b0` | `render_ring_flush` | advance/drain audio render ring |
| `1800653f0` | `get_dsp_state_ptr` | return &dsp state accessor |
| `180065400` | `modmatrix_apply_linear` | ctrl amount x 11 assign depths (linear) -> dests |
| `180065730` | `modmatrix_apply_bipolar` | ctrl amount x 11 depths (pitch/TVF/TVA scaled) |
| `180065bd0` | `modmatrix_apply_pitch1` | single-dest pitch bipolar mod-depth apply |
| `180065c70` | `cc67_soft_pedal` | binary; Rx gate 0x900, sets/clears bit 3 of part+0x08 |
| `180065e50` | `cc64_hold_damper` | writes part+0x462: raw value on half-damper tones (hdr byte 0xd bit2), else 0/0x7f; Rx gate 0x820 |
| `180065eb0` | `cc11_expression` | writes part+0x464 (Rx gates 0x810) |
| `1800661a0` | `cc66_sostenuto` | binary; captures sounding notes into bitmap part+0x260 + node flag +0x34 bit0; pedal-up releases state-2 groups |
| `180065cd0` | `caseD_7b` |  |
| `180065d00` | `caseD_7e` |  |
| `180065d50` | `caseD_7f` |  |
| `180065da0` | `caseD_78` |  |
| `180065f20` | `caseD_1` |  |
| `1800660a0` | `caseD_79` |  |
| `180066470` | `engine_all_parts_reset` | full reset: RPN slots + all part/voice structs |
| `180066750` | `part_voice_reset` | reset one part active voices/controller state |
| `180066790` | `sysex_reset_engine` | GM/GS reset dispatcher; install fn-ptrs, reset |
| `180066860` | `part_note_remap_refresh` | remap/re-trigger held notes on mode change |
| `180066c30` | `poly_aftertouch_apply` | poly key-pressure -> per-part mod matrix dests |
| `180066fe0` | `nrpn_apply` | NRPN switch: vib/TVF/TVA/drum params |
| `180067760` | `pitch_bend_apply` | pitch-bend -> bipolar mod matrix pitch dest |
| `1800678a0` | `channel_pressure_apply` | channel aftertouch -> mod matrix dest |
| `1800679b0` | `part_set_mono_legato` | mono/poly + legato voice-assign mode |
| `180067b00` | `part_set_tuning` | set per-part 16-bit tuning, update master scale |
| `180067c10` | `parts_reset_controllers` | reset per-part modulation/portamento state |
| `180067cc0` | `part_load_tone` | Program Change: load tone+subtones into part+0x228 |

## Note engine + GS SysEx DT1/RQ1 state machine

`0x180068000`–`0x180070000`. Note-on/off dispatch, hold pedal + sustain, voice-priority list, program change, and the GS SysEx parameter dump/receive state machine.

| Address | Name | Purpose |
|---------|------|---------|
| `180068060` | `part_apply_hold_pedal` | CC64: walk part voices, engage/release sustain +0x46f |
| `1800682d0` | `part_reassign_voice_node` | reset part fields, unlink+reinsert priority list |
| `180068400` | `note_on_dispatch` | note-on across parts: vel curve, drum/melodic trigger |
| `1800686c0` | `note_off_dispatch` | note-off across parts: clear velmap, mono/sustain |
| `1800688c0` | `voice_trigger_partials` | one part: final level, vel splits, partial alloc |
| `180068ae0` | `part_flush_held_notes` | clear sustained bitmap, mark partials for release |
| `180068c00` | `part_release_sustained` | pedal-up: replay note-offs for held notes |
| `180068cd0` | `part_clear_note_velmap` | fill part 128-byte note-velocity table with 0xff |
| `180068d70` | `part_count_active_notes` | count non-0xff entries in note-velocity table |
| `180068db0` | `voice_priority_list_insert` | insert part node into priority-sorted voice list |
| `180068e10` | `voice_priority_list_unlink` | unlink node from doubly-linked voice list |
| `180068e60` | `drum_part_program_change` | drum program change: resolve kit, propagate peers |
| `180068fe0` | `part_program_change` | melodic prog-change: resolve tone, copy tone header |
| `180069200` | `program_resolve_tone` | bank/prog -> tone index via map tables |
| `1800693a0` | `engine_reset_all_parts` | GM/GS reset: reinit parts, rebuild voice lists |
| `1800696d0` | `sysex_dispatch_reset` | reset sysex byte-dispatch fn ptr |
| `1800696f0` | `sysex_output_pump` | drive queued sysex bytes through handler fn-ptr |
| `1800697e0` | `sysex_prepare_dt1_reply` | parse RQ1/DT1 address, stage reply header |
| `180069b00` | `sysex_build_dump_address` | internal param addr -> GS sysex address bytes |
| `18006b100` | `sysex_receive_parse` | route incoming sysex by mfr byte, verify checksum |
| `18006b380` | `gs_reset_execute` | GS-reset data-set: reinit parts |
| `18006b4a0` | `sysex_select_param_map` | pick param-map table by model id + address |
| `18006ba60` | `sysex_handler_noop` | default byte consumer; flush TX when done |
| `18006bad0` | `sysex_advance_to_next_handler` | skip bytes to target index then jump |
| `18006bd00` | `sysex_data_request_dispatch` | RQ1 dispatcher: pick bulk dump to transmit |
| `18006c320` | `sysex_dump_param_table` | emit param table rows as DT1 sysex |
| `18006c490` | `sysex_dump_tone_names` | build tone-name strings per program, emit sysex |
| `18006c7c0` | `sysex_dump_rhythmset_names` | build rhythm-set-name strings, emit sysex |
| `18006d190` | `sysex_part_rx_channel_d190` | per-part MIDI rx channel, re-sort priority |
| `18006d700` | `sysex_sysparam_dispatch` | dispatch system/effect param addr 0x1000-0x207f |
| `18006e5e0` | `sysex_drum_dump_dispatch` | dispatch drum-setup dump addr via jump table |
| `18006f640` | `patch_dump_dispatch` | dispatch patch/part dump addr via jump table |

## GS SysEx part/system/effect parameter handlers

`0x180070000`–`0x180078000`. The GS Data-Set-1 handlers for every part, system-common, and reverb/chorus/delay/EQ/insertion-effect parameter.

| Address | Name | Purpose |
|---------|------|---------|
| `1800700a0` | `drum_setup_pitch_coarse` | drum setup per-note byte 0x380 |
| `180070220` | `drum_setup_rx_noteoff` | drum setup per-note flag 0x480 bit0 |
| `180070390` | `drum_setup_rx_noteon` | drum setup per-note flag 0x480 bit4 |
| `180070450` | `drum_setup_level` | drum setup per-note byte 0x400 |
| `1800705d0` | `drum_setup_assign_group` | drum setup per-note 0x50c clamp 1..4 |
| `180070750` | `drum_setup_panpot` | drum setup per-note byte 0x58c |
| `1800708b0` | `drum_setup_reverb_send` | drum setup per-note byte 0x60c |
| `180070ed0` | `gs_part_param_dispatch` | 2nd-level part-param sub-addr jump table |
| `180071150` | `sysex_master_tune` | system common master tune u16 |
| `180071360` | `sysex_master_volume` | system common master volume |
| `180071450` | `sysex_master_key_shift` | master key shift clamp 0x28..0x58 |
| `180071510` | `sysex_master_pan` | master panpot |
| `180071620` | `sysex_system_common_reset` | clear dump buf reset write idx |
| `180071690` | `sysex_master_patch_name` | 16-byte patch name |
| `1800717b0` | `sysex_system_common_dump` | pack system-common block into dump buf |
| `180071c20` | `sysex_reverb_macro` | reverb type/macro preset lookup |
| `180071d40` | `sysex_reverb_params` | reverb char/pre-lpf/level/time/fb/send |
| `180072040` | `sysex_chorus_macro` | chorus type/macro preset lookup |
| `180072120` | `sysex_chorus_params` | chorus params |
| `180072430` | `sysex_delay_macro` | delay type/macro preset lookup |
| `180072530` | `sysex_delay_params` | delay params |
| `180072960` | `sysex_eq_params` | 4-band EQ freq/gain |
| `180072b90` | `sysex_insertion_fx_type` | EFX type select via tone lookup |
| `180072d70` | `sysex_insertion_fx_params` | EFX param block |
| `180073250` | `sysex_part_drum_map` | part drum map 0x3d4/0x3d5 + rechannel |
| `180073420` | `sysex_part_rx_channel_3420` | part Rx channel 0x3d8 resets voice state |
| `180073690` | `sysex_part_rx_flags` | part Rx-controller bitmap u16 0x3d6 |
| `180073aa0` | `sysex_part_mono_poly` | part mono/poly mode bits 0x3d9 |
| `180073c50` | `sysex_part_assign_mode` | part assign/legato bits 0x3d9 |
| `180073dc0` | `sysex_part_voice_reserve` | poly/reserve 0x3d9+0x12 voice steal |
| `180074000` | `sysex_part_key_shift` | part key shift 0x3da clamp 0x28..0x58 |
| `180074110` | `sysex_part_pitch_offset` | part fine pitch 0x3db |
| `1800743b0` | `sysex_part_vib_rate` | part vibrato rate 0x3dc |
| `1800744b0` | `sysex_part_vib_delay` | part modify byte 0x3de |
| `1800745a0` | `sysex_part_tvf_cutoff` | part modify byte 0x3df |
| `180074690` | `sysex_part_vib_depth` | part modify byte 0x3dd |
| `180074790` | `sysex_part_tvf_resonance` | part modify byte 0x3e0 |
| `180074880` | `sysex_part_env_attack` | part modify byte 0x3e1 |
| `180074970` | `sysex_part_eq_bass` | part EQ low 0x3fa |
| `180074a70` | `sysex_part_eq_treble` | part EQ high 0x3fb |
| `180074c30` | `sysex_part_env_decay` | part modify 0x3e2 sets mono-flag |
| `180074d50` | `sysex_part_env_release` | part modify byte 0x3e3 |
| `180074e40` | `sysex_part_rx_bit0` | part flag 0x3ec bit0 |
| `180074ed0` | `sysex_part_rx_bit1` | part flag 0x3ec bit1 |
| `180075010` | `sysex_part_rx_bit2` | part flag 0x3ec bit2 |
| `1800751e0` | `sysex_part_pitchbend_val` | part pitch-bend value u16 0x448 |
| `180075320` | `sysex_part_mod_depth` | part 0x44a sets mono-flag |
| `180075450` | `sysex_part_ctrl_e4` | part modify byte 0x3e4 |
| `180075540` | `sysex_part_ctrl_e5` | part modify byte 0x3e5 |
| `180075630` | `sysex_part_ctrl_e6` | part modify byte 0x3e6 |
| `180075720` | `sysex_part_ctrl_e7` | part modify byte 0x3e7 |
| `180075810` | `sysex_part_ctrl_e8` | part modify byte 0x3e8 |
| `180075900` | `sysex_part_ctrl_e9` | part modify byte 0x3e9 |
| `1800759f0` | `sysex_part_ctrl_ea` | part modify byte 0x3ea |
| `180075ae0` | `sysex_part_ctrl_eb` | part modify byte 0x3eb |
| `180075c80` | `sysex_part_param_44b` | part byte 0x44b |
| `180075d10` | `sysex_part_scale_tuning` | 12-byte scale tuning 0x3ee..0x3f9 |
| `180075e90` | `sysex_part_name` | 13-byte part name 0x47c |
| `180075f80` | `sysex_part_param_44c` | part bool 0x44c |
| `180076000` | `sysex_part_control_matrix` | CC/bend/aftertouch dest matrix |
| `180076ad0` | `sysex_dump_block_advance` | bulk-dump block terminator/advance |
| `180076c30` | `sysex_part_bank_msb` | part bank/tone MSB 0x44d |
| `180076d20` | `sysex_part_bank_lsb` | part map/bank LSB 0x44e clamp 1..4 |
| `180076e40` | `sysex_part_param_44f` | part byte 0x44f |
| `180076e90` | `sysex_part_param_450` | part bool 0x450 |
| `180077170` | `sysex_part_param_451` | part bool 0x451 |
| `180077270` | `sysex_part_param_452` | part bool 0x452 |
| `180077970` | `sysex_part_tone_number` | tone select 0x24e via tone lookup |
| `180077af0` | `sysex_part_param_453` | part byte 0x453 |
| `180077b20` | `sysex_part_param_454` | part byte 0x454 |
| `180077b50` | `sysex_part_param_455` | part byte 0x455 |
| `180077b80` | `sysex_part_param_456` | part byte 0x456 |
| `180077bb0` | `sysex_part_param_457` | part byte 0x457 |
| `180077be0` | `sysex_part_param_458` | part byte 0x458 |
| `180077c10` | `sysex_part_param_459` | part byte 0x459 |
| `180077c40` | `sysex_part_param_45a` | part byte 0x45a |
| `180077c70` | `sysex_part_param_45b` | part byte 0x45b block end |
| `180077cc0` | `sysex_part_tone_load` | load melodic tone up to 3 layers into voice |

## GS SysEx bulk dump + reverb/chorus DSP config

`0x180078000`–`0x180080000`. Drum-set bulk transfer, universal (0x7E/0x7F) SysEx, and reverb/chorus DSP preset/coefficient config.

| Address | Name | Purpose |
|---------|------|---------|
| `1800782b0` | `sysex_drumset_dump_dispatch` | clear TX buf/select part buf, dispatch by addr |
| `1800783b0` | `drumset_select_part_buffer` | pick dump part buffer from part# |
| `1800784b0` | `sysex_drumset_name_12` | drum-set name block (12 chars, rx/tx) |
| `180078690` | `sysex_drumset_block_0x180` | per-note param block @part+0x180 rx/tx |
| `1800788c0` | `sysex_drumset_block_0x100` | per-note param block @part+0x100 rx/tx |
| `180078af0` | `sysex_drumset_block_0x200` | per-note param block @part+0x200 rx/tx |
| `180078d20` | `sysex_drumset_block_0x280` | per-note param block @part+0x280 rx/tx |
| `180078f50` | `sysex_drumset_block_0x300` | per-note param block @part+0x300 rx/tx |
| `180079180` | `sysex_drumset_block_0x380` | per-note block @part+0x380 |
| `1800793d0` | `sysex_drumset_rxnote_bit0` | per-note rx flag @part+0x480 bit0 |
| `180079610` | `sysex_drumset_rxnote_bit4` | per-note rx flag @part+0x480 bit4 |
| `1800796d0` | `sysex_drumset_block_0x400` | per-note block @part+0x400 |
| `180079980` | `sysex_drumset_rxnote_bit3` | per-note rx flag @part+0x480 bit3 |
| `180079a20` | `sysex_dt1_addr_dispatch` | GS DataSet1 master addr decode -> part base+handler |
| `18007af80` | `system_set_master_tune` | system: byte-swap 16b master tune write |
| `18007b050` | `system_set_reverb_chorus_eq` | write system FX block |
| `18007b290` | `system_set_block_then_partmap` | write system block, arm part map write |
| `18007b390` | `part_param_write_single` | single-part block param write |
| `18007b680` | `part_param_write_all` | GS per-part common write dispatch |
| `18007c430` | `sysex_bulkdump_tx_setup` | dump-request addr decode -> part base+TX handler |
| `18007c980` | `sysex_tx_buffer_reset` | zero 256-byte TX buffer |
| `18007c9d0` | `sysex_tx_finalize_checksum` | append Roland checksum+0xF7, enqueue |
| `18007cac0` | `sysex_tx_buffer_maybe_reset` | dec pending count, clear TX buf at zero |
| `18007cb10` | `sysex_tx_enqueue_output` | push TX buffer to output ring |
| `18007cbf0` | `univ_nonrealtime_sysex` | 0x7E: GM1 On/GM Off/GM2 On, reset |
| `18007cdd0` | `univ_realtime_sysex` | 0x7F: device-ctrl/tuning/ctrl-dest |
| `18007d030` | `sysex_scale_octave_tuning` | write 12 scale-tune bytes @part+0x3ee |
| `18007d190` | `sysex_key_based_inst_ctrl` | per-note level/pan/rev/cho |
| `18007d360` | `sysex_global_rev_cho_macro` | global reverb/chorus macros |
| `18007d5a0` | `sysex_dispatch_by_manufacturer` | top SysEx demux Roland/0x43/0x7E/0x7F |
| `18007d6c0` | `sysex_roland_addr_subdispatch` | Roland reception sub-state demux |
| `18007d910` | `sysex_gs_part_param_dispatch` | GS per-part param demux -> part+0x3dc.. |
| `18007dfa0` | `sysex_drum_setup_param` | GS drum-setup: tone-name match, drum pitch |
| `18007e010` | `sysex_master_tune_4nibble` | 4 nibbles -> 12-bit tune write |
| `18007e0f0` | `system_set_key_shift` | clamp, write system key shift |
| `18007e130` | `gs_reset` | GS Reset: reset all parts, rearm dispatch |
| `18007e230` | `all_parts_sound_off` | clear voice state all parts |
| `18007e2f0` | `reverb_type_select` | reverb macro lookup, set FX block |
| `18007e4d0` | `chorus_type_select` | chorus macro lookup, set FX block |
| `18007e5d0` | `part_set_bank_program` | bank+PC -> part+0x3d4/0x44d/0x3d5, drum sw |
| `18007e730` | `part_set_map_reset_voices` | part map, reset controllers, unlink voice |
| `18007e7f0` | `part_set_rx_flag` | part+0x3d9 bit7 |
| `18007e830` | `part_set_mono_poly` | part+0x3d9 mono/poly bits |
| `18007e880` | `part_set_assign_mode` | part+0x3d9/+0x12 voice-assign mode |
| `18007ea20` | `part_set_key_shift` | clamp -> part+0x3da key shift |
| `18007ea60` | `part_set_rx_channel` | 2 nibbles -> part+0x3db rx channel |
| `18007ecb0` | `part_set_rx_ctrl_flags` | set/clear part+0x3d6 rx-control bits |
| `18007ed10` | `part_set_rx_flag2` | part+0x3ec bit0 rx enable |
| `18007edf0` | `part_set_tva_partial_params` | write partial/tone params |
| `18007ef30` | `part_group_mute_toggle` | grouped parts: toggle mute bits |
| `18007ef90` | `part_group_set_param` | grouped parts: write part+0x463 |
| `18007eff0` | `caseD_0` |  |
| `18007f440` | `fx_load_reverb_preset` | load FX DSP reverb preset |
| `18007f490` | `fx_apply_reverb_chorus_type` | apply reverb/delay type: program DSP coeffs |
| `18007f6d0` | `fx_set_reverb_time_level` | set reverb/delay time+level |
| `18007f770` | `fx_set_chorus_params` | set chorus/delay time+level+LFO coeffs |
| `18007f840` | `fx_load_dsp_preset` | load FX coeff preset into DSP |
| `18007fa10` | `fx_ramp_eq_targets` | anti-zip ramp of EQ/mix values toward targets |
| `18007fb20` | `engine_alloc_init_voices` | malloc+init voice/effect struct arrays |

## MIDI ingest + per-tick modulation + audio render + display

`0x180080000`–`0x180090000`. Port FIFOs and the TG MIDI ring, LFO/pitch/TVF/TVA control-rate ramps, the SVF filters and chorus/reverb/biquad render cluster, and the ported LCD/boot-animation display subsystem.

| Address | Name | Purpose |
|---------|------|---------|
| `1800801c0` | `midi_port_reset_timeout_inject` | on port timeout inject reset/all-off |
| `180080450` | `midi_dispatch_flagged_ports` | drain flag-triggered port FIFOs, dispatch |
| `180080930` | `midi_port_enqueue` |  |
| `180080a90` | `midi_event_dispatch_record` | parse MIDI event, dispatch, record to ring |
| `180080c90` | `port_apply_default_cc_block` | push 384 default-CC events through port table |
| `180080d10` | `voice_release_or_kill` | per-voice release-env tick; kill when done |
| `180080e40` | `voice_block_process` |  |
| `180081410` | `part_mod_depth_recalc` | bitmask-gated recompute of part LFO/mod depths |
| `1800819c0` | `mod_pitch_control` |  |
| `180081a50` | `part_mod_accum_clear` | zero part mod-matrix accumulator/target fields |
| `180081b90` | `lfo_update` |  |
| `1800820f0` | `lfo_apply_depth` |  |
| `1800821e0` | `lfo_apply_depth_voices_fadein` | LFO1 per-voice depth apply during fade-in |
| `1800823b0` | `lfo2_update` | LFO2 per-control-tick update (mono variant) |
| `1800827d0` | `lfo2_apply_depth_full` | LFO2 mono depth apply at full fade |
| `1800828f0` | `lfo_wave_scale_tva` | signed 16x16 mul round +0xffff (TVA LFO scale) |
| `180082940` | `lfo_wave_scale_pitch` | signed 16x16 mul round +0x8000 (pitch/TVF LFO) |
| `180082990` | `lfo_pitch_depth_cents` |  |
| `180082a30` | `lfo_advance_waveform` |  |
| `180082e10` | `voice_pitch_block_init` | per-voice pitch/phase-increment init |
| `1800830e0` | `lfo_pitch_accumulate` |  |
| `1800831c0` | `voice_pitch_keyfollow` | apply key-follow bend delta |
| `180083270` | `voice_pitch_block_update` | per-block pitch env ramp + keyfollow |
| `180083680` | `pitch_env_ramp_segment` | 32-bit pitch-env segment ramp clamp 0x1f018 |
| `180083790` | `pitch_env_stage3_load` | stage 3: target <- voice+0x218 (unbiased base), time voice+0x208 |
| `180083800` | `pitch_env_stage2_load` | stage 2: target <- voice+0x214, time voice+0x206 |
| `180083870` | `pitch_env_stage1_load` | stage 1: start <- prior target, target <- voice+0x210, rate from stored ms |
| `1800838e0` | `tva_env_stage1_load` | TVA twin: target <- voice+0x1d2, time voice+0x1c6 |
| `180083960` | `tva_env_stage2_load` | TVA twin: target <- voice+0x1d4, time voice+0x1c8 |
| `1800839e0` | `tva_env_stage3_load` | TVA twin: target <- voice+0x1d6, time voice+0x1ca |
| `180083a70` | `env_ramp_segment` |  |
| `180083be0` | `voice_pan_smooth` | slew pan/send toward target |
| `180083db0` | `voice_expr_smooth` | slew expression/level via curve tbls |
| `180083f00` | `tvf_cutoff_compute` | TVF cutoff pipeline stages + add-LFO |
| `180083fc0` | `tvf_env_cutoff_update` | TVF env ramp + cutoff compute |
| `180084350` | `tvf_cutoff_add_lfo` |  |
| `180084880` | `tvf_env_ramp_segment` | TVF-env segment ramp snap on wrap |
| `1800849a0` | `voices_control_update` |  |
| `180084c60` | `engine_init_tasks_ports` | init task list, port structs, FIFOs |
| `180085140` | `fixed14_to_float` | decode packed 14-bit fixed to float |
| `1800851c0` | `fx_chorus_stage_l` | chorus/mod-delay stage 32-sample left |
| `180085460` | `fx_chorus_stage_r` | chorus/mod-delay stage right |
| `1800859c0` | `fx_param_set_pair` | search 3-entry fx param table set hi/lo |
| `180085a60` | `fx_reverb_alloc_init` | alloc reverb delay-line nodes seed coeffs |
| `180085c70` | `fx_param_set_coeff` | dispatch fx param idx set coeff |
| `180085f10` | `fx_param_set_delay` | dispatch fx param idx 8-tap set field |
| `180086140` | `fx_reverb_process` | reverb allpass/comb network 32-sample block |
| `180086620` | `fx_param_set_tap` | search 3-entry tap table set coeff |
| `180086690` | `fx_biquad_process` | 9-float-state biquad over block |
| `1800869a0` | `fx_param_dispatch` | big-switch fx CC/param setter |
| `180086c90` | `task_ready_enqueue` | insert task into scheduler ready list |
| `180086d80` | `port_emit_display_dump` | pack 0x27-byte bitmap dump into port FIFO |
| `180086ed0` | `display_emit_pages` | emit 3+13 display page blocks |
| `180086f60` | `display_select_bank` | select display bank emit |
| `180086fd0` | `display_compose_emit` | OR 4 bitmap sources checksum emit |
| `180087180` | `display_anim_countdown` | countdown timer call anim handler |
| `1800871d0` | `display_anim_advance` | advance display anim state-machine |
| `1800872f0` | `sysex_param_emit` | build Roland SysEx param msg checksum enqueue |
| `180087490` | `display_boot_frame_a` | boot display frame A |
| `1800874d0` | `display_boot_frame_b` | boot display frame B |
| `180087520` | `rom_selftest_and_banner` | checksum ROM regions set flags pick banner |
| `180087730` | `display_anim_reset_a` | reset anim counters handler ptrs |
| `1800877b0` | `display_page_load_a` | load 13 display page blocks A |
| `180087830` | `display_anim_step_a` | scrolling display anim step |
| `180087960` | `display_page_load_b` | load 13 display page blocks B |
| `1800879d0` | `display_blink_tick_a` | blink toggle emit page A |
| `180087ae0` | `display_page_load_c` | load 11 display page blocks C |
| `180087bc0` | `display_page_load_d` | load 13 display page blocks D |
| `180087c30` | `display_blink_tick_b` | blink toggle bank select emit B |
| `180087e00` | `display_boot_seq_a` | boot sequence pages + SysEx params A |
| `180087ef0` | `display_boot_step` | staged boot animation step |
| `180088140` | `display_anim_reset_b` | reset anim counters handler ptrs B |
| `1800881c0` | `display_frame_flush` | emit one blank frame advance anim |
| `1800881f0` | `display_boot_seq_b` | boot sequence pages + SysEx params B |
| `1800883e0` | `midi_active_sense_blink` | periodic all-sound-off sensing toggle |
| `180088480` | `display_boot_step_b` | staged boot animation step B |
| `1800887c0` | `get_local_time_ms` |  |
| `180088820` | `debug_sprintf` | vsprintf wrapper debug strings |
| `1800888a0` | `TG_initialize` |  |
| `180088b20` | `TG_terminate` |  |
| `180088b40` | `TG_activate` |  |
| `180088b90` | `TG_deactivate` |  |
| `180088bb0` | `TG_setSampleRate` |  |
| `180088bf0` | `TG_setMaxBlockSize` |  |
| `180088ca0` | `TG_Process` |  |
| `1800890b0` | `TG_isFatalError` |  |
| `1800890c0` | `TG_getErrorStrings` |  |
| `1800891e0` | `TG_flushMidi` |  |
| `1800892d0` | `TG_PMidiIn` |  |
| `180089370` | `TG_ShortMidiIn` |  |
| `1800895c0` | `TG_LongMidiIn` |  |
| `180089760` | `TG_XPsetSystemConfig` |  |
| `1800897b0` | `TG_XPgetCurSystemConfig` |  |
| `1800897c0` | `fx_buffer_alloc` | alloc float delay buffer denormal seed |
| `180089830` | `fx_delayline_wrap` |  |
| `1800898d0` | `fx_reg_write` |  |
| `180089a10` | `voice_compute_mod_rates` |  |
| `180089b60` | `voice_setup_sample_playback` |  |
| `18008a1a0` | `voice_ramp_targets_writeback` | set all 4 per-voice ctrl-ramp targets |
| `18008a490` | `voice_ramp_target_amp` | set voice amp/level ramp target |
| `18008a570` | `voice_ramp_target_pitch` | set voice pitch ramp target |
| `18008a5e0` | `voice_ramp_target_aux` | set voice aux ramp target |
| `18008a6c0` | `voice_pool_scan` |  |
| `18008ab80` | `TG_XPgetCurTotalRunningVoices` |  |
| `18008ab90` | `midi_drain_ready_to_ports` |  |
| `18008ac20` | `src_get_field8` | SRC vtable return field+8 |
| `18008ac30` | `src_set_field_c` | SRC vtable store field+0xc |
| `18008ac40` | `src_set_ratio` | SRC vtable ratio = base/rate |
| `18008ac50` | `src_get_field_c` | SRC vtable copy out field+0xc |
| `18008ac60` | `src_reset_state` | SRC vtable reset coeffs denormal seed |
| `18008aca0` | `tg_output_filter` |  |
| `18008aec0` | `voice_report_finished` | scan voice-done flag signal part note-off |
| `18008af50` | `voice_output_accumulate` | mix per-voice output into 64 bus accumulators |
| `18008b1d0` | `render_block` |  |
| `18008b510` | `voice_set_ramp_target_0` |  |
| `18008b660` | `voice_set_ramp_target_1` |  |
| `18008b790` | `voice_set_ramp_target_2` |  |
| `18008b9e0` | `voice_count_running` | count voices with run-flag bit0 |
| `18008bba0` | `fx_send_mix` | scale+add source into 2 send buffers |
| `18008bd30` | `output_bus_mix` | sum 5 voice-group buffers scale to output |
| `18008bf50` | `fx_set_coef_bank_a` | set fx coeff bank A entry |
| `18008bf80` | `fx_set_coef_bank_b` | set fx coeff bank B entry |
| `18008bfb0` | `fx_set_dry_level` | set dry-mix level |
| `18008bfe0` | `fx_set_send_level` | set send/reverb level |
| `18008c010` | `fx_engine_init` | alloc delay lines clear coeffs + delay mem |
| `18008c2c0` | `fx_process_block` |  |
| `18008cb10` | `sched_timer_run` | run due timer-heap entries re-sift |
| `18008cc20` | `sched_heap_sift_a` | timer binary-heap sift half-step |
| `18008cc80` | `sched_heap_sift_down` | timer binary-heap sift-down |
| `18008cd20` | `sched_heap_insert` | insert entry into timer heap |
| `18008cdc0` | `sched_heap_remove` | remove/rebalance timer heap entry |
| `18008ce70` | `tvf_svf_render_alt` |  |
| `18008d0a0` | `svf_render_lp` | Chamberlin SVF lowpass tap 8-sample |
| `18008d2d0` | `svf_render_hp` | Chamberlin SVF highpass tap 8-sample |
| `18008d520` | `svf_render_bp` | Chamberlin SVF bandpass tap 8-sample |
| `18008d740` | `svf_render_notch` | Chamberlin SVF notch tap 8-sample |
| `18008d9a0` | `tvf_svf_render` |  |
| `18008dd90` | `midi_track_play_a` | read port FIFO running-status parse dispatch |
| `18008de60` | `midi_track_play_b` | port FIFO parse w/ channel-change gating |
| `18008e050` | `midi_track_finish` | reset track state on end |
| `18008e960` | `midi_runstatus_step` | running-status state-machine transition |
| `18008ea40` | `midi_runstatus_step_ch` | running-status step w/ active-channel switch |
| `18008ebd0` | `note_alloc_process` | init voice/part pools process alloc/note events |
| `18008f020` | `tg_start_pending_voices` |  |
| `18008f0d0` | `control_tick_dispatch` |  |
| `18008f1e0` | `param_tables_init` | init 0x120 controller/param tables |
| `18008f340` | `param_set_msb` | write param MSB route to fx delay coeff |
| `18008f4e0` | `param_set_lsb` | write param LSB route to fx setters |
| `18008f5c0` | `param_set_14bit` | write 14-bit param via msb+lsb pair |
| `18008f640` | `voice_start` |  |
| `18008f8a0` | `voice_env_retrigger` | restore voice amp/pitch ramp targets on retrigger |
| `18008fa00` | `voice_begin_fadeout` | set voice to fast-fade state |
| `18008fab0` | `voice_fadeout_tick` | track fade level kill voice when silent |
| `18008fbb0` | `prng_lfsr` |  |
| `18008fc30` | `param_tables_reset_defaults` | clear param tables load GM defaults |

## CRT / host runtime tail

`0x180090000`–`0x1800a0000`. MSVC/CRT boilerplate (DllMain, onexit, exception glue) plus the ROM version self-test.

| Address | Name | Purpose |
|---------|------|---------|
| `1800903c0` | `rom_version_selftest` | decode bit-scrambled table, verify Roland XP/SC-GS version strings |
| `180090560` | `__security_check_cookie` |  |
| `180090584` | `crt_init_condvar_support` | init statics critsec, resolve ConditionVariable APIs |
| `1800906e4` | `crt_cleanup_condvar_support` | DeleteCriticalSection + CloseHandle fallback event |
| `18009070c` | `_Init_thread_footer` |  |
| `18009076c` | `_Init_thread_header` |  |
| `1800907d4` | `_Init_thread_notify` |  |
| `180090824` | `_Init_thread_wait` |  |
| `18009089c` | `__raise_securityfailure` |  |
| `1800908d0` | `__report_gsfailure` |  |
| `1800909a4` | `__report_rangecheckfailure` |  |
| `1800909b8` | `__report_securityfailure` |  |
| `180090a54` | `capture_current_context` |  |
| `180090ac4` | `capture_previous_context` |  |
| `180090b38` | `free` |  |
| `180090b40` | `alloc_retry` |  |
| `180090b48` | `msvc_type_info_dtor` | type_info scalar-deleting destructor |
| `180090b74` | `alloc_retry` |  |
| `180090bb0` | `crt_dllmain_reason_dispatch` | switch DLL reason -> attach/detach/thread handlers |
| `180090c00` | `crt_dllmain_process_attach` | init crt, _initterm, TLS/ctors on PROCESS_ATTACH |
| `180090d1c` | `crt_dllmain_process_detach` | uninit crt, run dtors on PROCESS_DETACH |
| `180090dac` | `dllmain_dispatch` |  |
| `180090ee0` | `entry` |  |
| `180090f20` | `__scrt_acquire_startup_lock` |  |
| `180090f5c` | `__scrt_dllmain_after_initialize_c` |  |
| `180090f90` | `crt_init_onexit_tables` | wrapper -> onexit table init |
| `180090fa8` | `crt_dllmain_thread_attach` | THREAD_ATTACH handler |
| `180090fd0` | `crt_dllmain_thread_detach` | THREAD_DETACH handler |
| `180090fe8` | `__scrt_dllmain_exception_filter` |  |
| `180091048` | `__scrt_dllmain_uninitialize_c` |  |
| `180091078` | `crt_uninitialize_stub` | pair of no-op uninit callbacks |
| `18009108c` | `__scrt_initialize_crt` |  |
| `1800910d8` | `crt_init_onexit_tables_impl` | encode/init _onexit table pointers |
| `1800911b0` | `__scrt_is_nonwritable_in_current_image` |  |
| `18009124c` | `__scrt_release_startup_lock` |  |
| `180091270` | `__scrt_uninitialize_crt` |  |
| `18009129c` | `_onexit` |  |
| `1800912ec` | `atexit` |  |
| `180091304` | `crt_reset_onexit_head` | zero onexit-table global |
| `18009130c` | `__scrt_fastfail` |  |
| `180091458` | `free` |  |
| `180091460` | `msvc_bad_alloc_ctor_copy` | std::bad_alloc copy constructor |
| `1800914a0` | `msvc_bad_alloc_ctor` | std::bad_alloc default ctor |
| `1800914c0` | `msvc_bad_array_len_ctor_copy` | std::bad_array_new_length copy ctor |
| `180091500` | `msvc_bad_array_len_ctor` | std::bad_array_new_length default ctor |
| `180091520` | `exception` |  |
| `18009156c` | `msvc_exception_dtor` | std::exception scalar-deleting destructor |
| `1800915b0` | `msvc_throw_bad_alloc` | construct + _CxxThrowException bad_alloc |
| `1800915d0` | `msvc_throw_bad_array_len` | construct + _CxxThrowException bad_array_new_length |
| `1800915f0` | `msvc_exception_what` | std::exception::what accessor |
| `180091604` | `__security_init_cookie` |  |
| `1800916b0` | `crt_init_typeinfo_list` | InitializeSListHead for type_info list |
| `1800916c0` | `crt_destroy_typeinfo_list` | __std_type_info_destroy_list |
| `1800916cc` | `crt_get_appflag_ptr_b` | return &flag global |
| `1800916d4` | `crt_set_app_type_flags` | set bits in two runtime flag globals |
| `1800916f0` | `crt_get_dllmain_callback` | return &user dllmain callback ptr |
| `1800916f8` | `crt_run_init_callbacks_a` | iterate+call init fn-ptr table A |
| `180091734` | `crt_run_init_callbacks_b` | iterate+call init fn-ptr table B |
| `180091770` | `__isa_available_init` |  |
| `18009192c` | `__scrt_is_ucrt_dll_in_use` |  |
| `180091940` | `__CxxFrameHandler3` |  |
| `180091952` | `memset` |  |
| `180091958` | `__std_exception_copy` |  |
| `18009195e` | `__std_exception_destroy` |  |
| `180091964` | `_CxxThrowException` |  |
| `18009196a` | `__std_type_info_destroy_list` |  |
| `180091970` | `free` |  |
| `180091976` | `malloc` |  |
| `18009197c` | `_callnewh` |  |
| `180091982` | `_initterm` |  |
| `180091988` | `_initterm_e` |  |
| `18009198e` | `_seh_filter_dll` |  |
| `180091994` | `_configure_narrow_argv` |  |
| `18009199a` | `_initialize_narrow_environment` |  |
| `1800919a0` | `_initialize_onexit_table` |  |
| `1800919a6` | `_register_onexit_function` |  |
| `1800919ac` | `_execute_onexit_table` |  |
| `1800919b2` | `_crt_atexit` |  |
| `1800919b8` | `_cexit` |  |
| `1800919be` | `IsProcessorFeaturePresent` |  |
| `1800919c4` | `crt_stub_return_true` | no-op returning 1 |
| `1800919c8` | `__GSHandlerCheck` |  |
| `1800919e8` | `__GSHandlerCheckCommon` |  |
| `180091ac3` | `memcpy` |  |
| `180091ae0` | `_guard_dispatch_icall` |  |
| `180091b4a` | `crt_unwind_release_lock` | unwind funclet: release startup lock |
| `180091b61` | `crt_unwind_uninit_release` | unwind funclet: uninit + release lock |
| `180091b7d` | `crt_unwind_dllmain_filter` | unwind funclet: dllmain exception filter |
| `180091bb3` | `crt_dllmain_filter_is_av` | test exception code == 0xC0000005 |

## Data labels

| Address | Label | What it is |
|---------|-------|------------|
| `181893930` | `g_delay_preset_tbl` | delay macro preset table |
| `18189566e` | `g_fx_type_algo_col` | per-row algo-index column of EFX type map |
| `1819a0108` | `g_reverb_macro_table` | reverb-type match table |
| `1819a0248` | `g_reverb_preset_tbl` | reverb macro preset table |
| `1819a0550` | `g_rx_flag_bitmask_tbl` | u16 bitmasks for part Rx-flag 0x3d6 |
| `1819a0830` | `g_chorus_macro_table` | chorus-type match table |
| `1819a2890` | `g_tvf_env_level_curve` | abs(level-0x40) TVF env level curve |
| `1819a2fa0` | `g_pitch_split_coarse` | coarse byte of pitch-env value table |
| `1819a3020` | `g_pitch_split_fine` | fine byte of pitch-env value table |
| `1819a7a00` | `g_tvf_env_startphase` | TVF env start-phase table[0..10] |
| `1819a9d80` | `g_bit_mask_lut` | 1<<(i&0x1f) shared bitmask helper |
| `1819f28b0` | `g_prog_to_col` | program -> map column (0xff=none) |
| `1819f2e30` | `g_bank_to_row` | bank(CC0) -> map row (0xff=none) |
| `1819f32b0` | `g_tonemap_index` | [row][col] -> tone index (0x8000+=none) |
| `181a03620` | `g_kf_tvfrate2` | second TVF env-rate key-follow table |
| `181a10140` | `g_voice_ramp_amp` | per-voice amp/level ctrl-ramp SoA |
| `181a10740` | `g_voice_ramp_cutoff` | per-voice TVF-cutoff ctrl-ramp SoA |
| `181a18f70` | `g_output_bus_accum` | 64x32-float output/effect bus accumulators |
| `181a1b570` | `g_voice_dpcm_state` | per-voice DPCM sampler state 0x50 stride |
| `181a1cbf0` | `g_voice_ramp_pitch` | per-voice pitch ctrl-ramp SoA |
| `181a1dd30` | `g_note_lru_tail` | note-group priority list tail |
| `181a1dd38` | `g_note_lru_head` | note-group priority list head |
| `181a1dd60` | `g_partial_shared_node_freelist` | 0xa0 env/mod node freelist |
| `181a1dd68` | `g_partial_voice_freelist` | partial voice node freelist |
| `181a1dde0` | `g_cur_partial_params` | 0x6e-stride partial param base |
| `181a1debe` | `g_sysex_mode` | 0x11=RQ1/dump build, 0x12=DT1 write |
| `181a1e292` | `g_active_sound_map` | SC-55/88/88Pro map id |
| `181a1f5a8` | `g_tva_base_level` | scratch TVA base level |
| `181a20288` | `g_modmatrix_dirty_mask` | per-dest dirty bitmask from modmatrix helpers |
| `181a21290` | `g_part_note_velmap` | per-part 128-byte note-velocity table (0xff=inactive) |
| `181a22291` | `g_midi_note` | current MIDI note number |
| `181a22292` | `g_midi_channel` | current MIDI channel |
| `181a222a0` | `g_part_array_base` | base of part structs stride 0x488 |
| `181a222b8` | `g_sysex_write_cursor` | DataSet1 write target pointer |
| `181a2251c` | `g_sysex_addr_idx` | sysex running address/index counter |
| `181a22570` | `g_cur_part_base` | active part struct base ptr |
| `181a22660` | `g_port_fifo_base` | per-port MIDI FIFO array stride 0xc0 |
| `181a226d0` | `g_voice_scratch` | voice_block_process whole-voice snapshot |
| `181a227d8` | `g_voice_lfo_mod` | per-voice LFO mod SoA pitch/TVF/TVA |
| `181a22808` | `g_lfo_out` | current LFO waveform output sample |
| `181a2280a` | `g_lfo_phase` | LFO1 phase accumulator |
| `181a229e0` | `g_fx_delay_mem` | 64K-float shared FX delay memory |
| `181a73ca0` | `g_sysex_tx_buf` | 256-byte sysex transmit buffer |

---

*Regenerate/extend via the naming pass in `tools/ghidra_scripts/RenameBulk2607.java`
and the other `Rename*.java` scripts. Add newly-identified functions there, re-run headless,
and regenerate `SCCore.decompiled.c`.*
