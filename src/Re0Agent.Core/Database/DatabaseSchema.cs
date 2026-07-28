namespace Re0Agent.Core.Database;

public static class DatabaseSchema
{
    public static readonly string[] ExpectedTableNames =
    [
        "global_state",
        "world_map_points",
        "map_elements",
        "factions",
        "protagonist_info",
        "important_npc",
        "inventory",
        "equipment",
        "quests",
        "chronicle",
        "character_memory",
        "timeline_branches",
        "timeline_events",
        "save_points",
        "projection_checkpoints",
        "projection_entity_versions",
        "projection_command_log",
        "world_scheduler_jobs",
        "world_runtime_state",
        "pending_directions",
        "reveal_queue",
        "lorebook_condition_entries",
        "character_card_sources",
        "lorebook_sources",
        "scene_states",
        "character_agency_states",
        "story_threads",
        "memory_embeddings",
        "pacing_state_cache",
        "director_plan_versions",
        "director_pulses",
        "death_return_log",
        "agent_config",
        "protagonist_templates",
        "api_routing",
        "chat_sessions"
    ];

    public static readonly string[] CreateStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS global_state (
          row_id INTEGER PRIMARY KEY CHECK(row_id = 1),
          current_location TEXT NOT NULL,
          current_minor_region TEXT NOT NULL,
          current_major_region TEXT NOT NULL,
          prev_scene_time TEXT CHECK(prev_scene_time IS NULL OR (prev_scene_time GLOB '*月-*日-*:*' AND instr(prev_scene_time, '上午') = 0 AND instr(prev_scene_time, '下午') = 0 AND instr(prev_scene_time, '早晨') = 0)),
          elapsed_time TEXT NOT NULL,
          cur_time TEXT NOT NULL CHECK(cur_time GLOB '*月-*日-*:*' AND instr(cur_time, '上午') = 0 AND instr(cur_time, '下午') = 0 AND instr(cur_time, '早晨') = 0),
          current_chapter INTEGER NOT NULL DEFAULT 1 CHECK(current_chapter >= 1),
          is_lewd TEXT NOT NULL DEFAULT '否' CHECK(is_lewd IN ('是', '否'))
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS world_map_points (
          row_id INTEGER PRIMARY KEY,
          location_name TEXT NOT NULL UNIQUE,
          minor_region TEXT NOT NULL,
          major_region TEXT NOT NULL,
          location_type TEXT NOT NULL,
          environment_desc TEXT NOT NULL CHECK(LENGTH(environment_desc) <= 60),
          importance TEXT NOT NULL CHECK(importance IN ('核心', '重要', '普通')),
          exploration_status TEXT NOT NULL CHECK(exploration_status IN ('未探索', '部分探索', '已探索'))
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS map_elements (
          row_id INTEGER PRIMARY KEY,
          element_name TEXT NOT NULL UNIQUE,
          element_type TEXT NOT NULL CHECK(element_type IN ('剧情物品', '威胁', '龙套', '地标')),
          location_name TEXT NOT NULL,
          element_desc TEXT NOT NULL CHECK(LENGTH(element_desc) <= 40),
          status_text TEXT NOT NULL,
          interaction_options TEXT NOT NULL CHECK(TRIM(interaction_options) <> '')
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS factions (
          row_id INTEGER PRIMARY KEY,
          faction_name TEXT NOT NULL UNIQUE,
          description TEXT NOT NULL CHECK(LENGTH(description) <= 60),
          leader TEXT,
          relations_text TEXT,
          headquarters TEXT
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS protagonist_info (
          row_id INTEGER PRIMARY KEY CHECK(row_id = 1),
          char_id INTEGER NOT NULL DEFAULT 0,
          name TEXT NOT NULL,
          gender TEXT NOT NULL,
          age INTEGER NOT NULL CHECK(age >= 0),
          appearance TEXT NOT NULL CHECK(LENGTH(appearance) <= 60),
          identity_text TEXT NOT NULL CHECK(LENGTH(identity_text) <= 40),
          self_status TEXT NOT NULL DEFAULT '正常',
          location_name TEXT NOT NULL,
          base_attributes TEXT NOT NULL,
          special_attributes TEXT,
          resources_text TEXT,
          hp INTEGER NOT NULL DEFAULT 100,
          max_hp INTEGER NOT NULL DEFAULT 100,
          mp INTEGER NOT NULL DEFAULT 0,
          max_mp INTEGER NOT NULL DEFAULT 0,
          stamina INTEGER NOT NULL DEFAULT 100,
          max_stamina INTEGER NOT NULL DEFAULT 100,
          armor INTEGER NOT NULL DEFAULT 0,
          skills_json TEXT
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS important_npc (
          row_id INTEGER PRIMARY KEY,
          char_id INTEGER NOT NULL DEFAULT 0,
          name TEXT NOT NULL UNIQUE,
          gender TEXT NOT NULL,
          age INTEGER NOT NULL CHECK(age >= 0),
          brief_intro TEXT NOT NULL CHECK(LENGTH(brief_intro) <= 30),
          appearance TEXT NOT NULL CHECK(LENGTH(appearance) <= 60),
          identity_text TEXT NOT NULL CHECK(LENGTH(identity_text) <= 40),
          base_attributes TEXT NOT NULL,
          special_attributes TEXT,
          location_name TEXT NOT NULL,
          relations_text TEXT,
          interaction_options TEXT,
          past_experience TEXT NOT NULL CHECK(LENGTH(past_experience) <= 600),
          self_status TEXT NOT NULL DEFAULT '正常',
          hp INTEGER NOT NULL DEFAULT 100,
          max_hp INTEGER NOT NULL DEFAULT 100,
          mp INTEGER NOT NULL DEFAULT 0,
          max_mp INTEGER NOT NULL DEFAULT 0,
          stamina INTEGER NOT NULL DEFAULT 100,
          max_stamina INTEGER NOT NULL DEFAULT 100,
          armor INTEGER NOT NULL DEFAULT 0,
          skills_json TEXT
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS inventory (
          row_id INTEGER PRIMARY KEY,
          item_name TEXT NOT NULL UNIQUE CHECK(LENGTH(item_name) <= 10),
          item_type TEXT NOT NULL,
          quantity INTEGER NOT NULL DEFAULT 1 CHECK(quantity >= 0),
          quality TEXT NOT NULL CHECK(quality IN ('普通', '优秀', '稀有', '史诗', '传说', '神话')),
          description TEXT NOT NULL CHECK(LENGTH(description) <= 60)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS equipment (
          row_id INTEGER PRIMARY KEY,
          equipment_name TEXT NOT NULL UNIQUE,
          equipment_type TEXT NOT NULL,
          quality TEXT NOT NULL CHECK(quality IN ('普通', '优秀', '稀有', '史诗', '传说', '神话')),
          status_text TEXT NOT NULL CHECK(status_text IN ('已装备', '闲置')),
          description TEXT NOT NULL CHECK(LENGTH(description) <= 40)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS quests (
          row_id INTEGER PRIMARY KEY,
          quest_name TEXT NOT NULL UNIQUE,
          quest_type TEXT NOT NULL CHECK(quest_type IN ('主线', '支线', '日常')),
          priority_level TEXT NOT NULL CHECK(priority_level IN ('紧急', '重要', '普通')),
          target_desc TEXT NOT NULL CHECK(LENGTH(target_desc) <= 100),
          progress_text TEXT NOT NULL CHECK(progress_text = '0%' OR progress_text GLOB '[1-9]%' OR progress_text GLOB '[1-9][0-9]%' OR progress_text = '100%'),
          status_tag TEXT NOT NULL CHECK(status_tag IN ('进行中', '已完成', '已失败', '已放弃')),
          source_text TEXT,
          reward_text TEXT
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chronicle (
          row_id INTEGER PRIMARY KEY,
          code_index TEXT NOT NULL UNIQUE CHECK(code_index GLOB 'AM[0-9][0-9][0-9][0-9]'),
          time_span TEXT NOT NULL CHECK(time_span GLOB '????-??-?? ??:?? ~ ????-??-?? ??:??'),
          summary TEXT NOT NULL CHECK(LENGTH(summary) <= 30),
          chronicle_text TEXT NOT NULL CHECK(LENGTH(chronicle_text) >= 100 AND LENGTH(chronicle_text) <= 2000)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS character_memory (
          row_id INTEGER PRIMARY KEY,
          owner_character_id TEXT NOT NULL,
          source_event_id TEXT NOT NULL,
          world_time TEXT NOT NULL,
          world_epoch INTEGER NOT NULL,
          observation_channel TEXT,
          confidence TEXT NOT NULL DEFAULT '确知',
          visibility_scope TEXT NOT NULL,
          memory_type TEXT NOT NULL,
          retain_on_rewind INTEGER NOT NULL DEFAULT 0 CHECK(retain_on_rewind IN (0, 1)),
          memory_text TEXT NOT NULL,
          emotional_state TEXT,
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS timeline_branches (
          branch_id TEXT PRIMARY KEY,
          session_id INTEGER NOT NULL REFERENCES chat_sessions(session_id),
          parent_branch_id TEXT REFERENCES timeline_branches(branch_id),
          parent_event_id TEXT,
          branch_reason TEXT NOT NULL,
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS timeline_events (
          event_id TEXT PRIMARY KEY,
          branch_id TEXT NOT NULL REFERENCES timeline_branches(branch_id),
          parent_event_id TEXT REFERENCES timeline_events(event_id),
          sequence INTEGER,
          world_epoch INTEGER NOT NULL,
          scene_id TEXT,
          actor_id TEXT,
          target_id TEXT,
          event_type TEXT NOT NULL,
          content TEXT NOT NULL,
          state_change_set TEXT,
          direct_observers TEXT NOT NULL DEFAULT '[]',
          potential_learners TEXT NOT NULL DEFAULT '[]',
          observability_computed INTEGER NOT NULL DEFAULT 0 CHECK(observability_computed IN (0, 1)),
          visibility_scope TEXT NOT NULL DEFAULT '[]',
          revealed_event_cursors TEXT NOT NULL DEFAULT '[]',
          status TEXT NOT NULL DEFAULT 'Draft' CHECK(status IN ('Draft', 'Committed', 'Interrupted', 'Superseded')),
          pacing_metadata TEXT,
          trigger_cause TEXT,
          causal_parent_event_id TEXT,
          real_created_at TEXT NOT NULL,
          world_time TEXT NOT NULL,
          UNIQUE(branch_id, sequence)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS save_points (
          save_id INTEGER PRIMARY KEY,
          branch_id TEXT NOT NULL REFERENCES timeline_branches(branch_id),
          event_id TEXT NOT NULL REFERENCES timeline_events(event_id),
          world_epoch INTEGER NOT NULL,
          trigger_reason TEXT NOT NULL,
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS projection_checkpoints (
          checkpoint_id INTEGER PRIMARY KEY,
          branch_id TEXT NOT NULL REFERENCES timeline_branches(branch_id),
          event_sequence INTEGER NOT NULL,
          world_epoch INTEGER NOT NULL,
          projection_data TEXT NOT NULL,
          created_at TEXT NOT NULL,
          UNIQUE(branch_id, event_sequence, world_epoch)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS projection_entity_versions (
          version_id INTEGER PRIMARY KEY,
          projection TEXT NOT NULL,
          entity_id TEXT NOT NULL,
          version INTEGER NOT NULL,
          UNIQUE(projection, entity_id)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS projection_command_log (
          command_id TEXT PRIMARY KEY,
          event_id TEXT NOT NULL REFERENCES timeline_events(event_id),
          applied_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS world_scheduler_jobs (
          job_id INTEGER PRIMARY KEY,
          session_id INTEGER NOT NULL REFERENCES chat_sessions(session_id),
          job_type TEXT NOT NULL,
          scheduled_world_time TEXT NOT NULL,
          payload TEXT,
          status TEXT NOT NULL DEFAULT 'Pending',
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS world_runtime_state (
          session_id INTEGER PRIMARY KEY REFERENCES chat_sessions(session_id),
          generation_backpressure_factor REAL NOT NULL DEFAULT 1.0 CHECK(generation_backpressure_factor > 0 AND generation_backpressure_factor <= 1),
          foreground_pending_count INTEGER NOT NULL DEFAULT 0,
          foreground_lag_seconds REAL NOT NULL DEFAULT 0,
          current_scene_budget_used INTEGER NOT NULL DEFAULT 0,
          input_activity_started_at TEXT,
          input_event_count INTEGER NOT NULL DEFAULT 0,
          foreground_admission_limit INTEGER NOT NULL DEFAULT 1,
          budget_window_started_at TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS pending_directions (
          direction_id INTEGER PRIMARY KEY,
          session_id INTEGER NOT NULL REFERENCES chat_sessions(session_id),
          source_event_id TEXT NOT NULL REFERENCES timeline_events(event_id),
          content TEXT NOT NULL,
          precondition_chain TEXT NOT NULL DEFAULT '[]',
          earliest_world_time TEXT NOT NULL DEFAULT '',
          completion_progress REAL NOT NULL DEFAULT 0,
          block_reason TEXT,
          status TEXT NOT NULL DEFAULT 'Pending',
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS reveal_queue (
          queue_id INTEGER PRIMARY KEY,
          session_id INTEGER NOT NULL REFERENCES chat_sessions(session_id),
          event_id TEXT NOT NULL REFERENCES timeline_events(event_id),
          status TEXT NOT NULL DEFAULT 'Pending',
          occurred_world_time TEXT NOT NULL,
          importance REAL NOT NULL DEFAULT 0,
          story_thread_id INTEGER,
          allowed_visibility_scope TEXT NOT NULL DEFAULT '[]',
          causal_distance INTEGER NOT NULL DEFAULT 0,
          latest_reveal_world_time TEXT NOT NULL,
          must_reveal INTEGER NOT NULL DEFAULT 0 CHECK(must_reveal IN (0, 1)),
          coalesced_event_ids TEXT NOT NULL DEFAULT '[]',
          merge_category TEXT NOT NULL DEFAULT 'other',
          created_at TEXT NOT NULL,
          UNIQUE(session_id, event_id)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS lorebook_condition_entries (
          entry_id INTEGER PRIMARY KEY,
          source_key TEXT NOT NULL,
          source_order INTEGER NOT NULL,
          legacy_condition TEXT NOT NULL,
          fact_predicate TEXT NOT NULL,
          content TEXT NOT NULL,
          created_at TEXT NOT NULL,
          UNIQUE(source_key, source_order, legacy_condition, content)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS character_card_sources (
          source_id INTEGER PRIMARY KEY,
          source_key TEXT NOT NULL UNIQUE,
          format TEXT NOT NULL,
          name TEXT NOT NULL,
          description TEXT,
          personality TEXT,
          scenario TEXT,
          system_prompt TEXT,
          post_history_instructions TEXT,
          example_dialogues TEXT,
          creator TEXT,
          character_version TEXT,
          tags TEXT,
          first_mes TEXT,
          alternate_greetings TEXT NOT NULL DEFAULT '[]',
          character_book TEXT,
          extensions TEXT NOT NULL DEFAULT '{}',
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS lorebook_sources (
          source_id INTEGER PRIMARY KEY,
          source_key TEXT NOT NULL UNIQUE,
          format TEXT NOT NULL,
          entries TEXT NOT NULL,
          extensions TEXT NOT NULL DEFAULT '{}',
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS scene_states (
          scene_id TEXT PRIMARY KEY,
          region_id TEXT,
          is_foreground INTEGER NOT NULL DEFAULT 0,
          summary TEXT NOT NULL DEFAULT '{}',
          last_world_time TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS character_agency_states (
          character_id TEXT PRIMARY KEY,
          scene_id TEXT,
          current_goal TEXT,
          next_action_world_time TEXT NOT NULL,
          fidelity TEXT NOT NULL DEFAULT 'foreground',
          status TEXT NOT NULL DEFAULT 'Active'
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS story_threads (
          thread_id INTEGER PRIMARY KEY,
          scope TEXT NOT NULL,
          status TEXT NOT NULL DEFAULT 'Active',
          urgency REAL NOT NULL DEFAULT 0,
          prerequisites TEXT NOT NULL DEFAULT '[]',
          updated_world_time TEXT NOT NULL,
          last_plan_version_id INTEGER,
          modified_count INTEGER NOT NULL DEFAULT 0
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS memory_embeddings (
          memory_row_id INTEGER PRIMARY KEY REFERENCES character_memory(row_id),
          model_id TEXT NOT NULL,
          dimensions INTEGER NOT NULL,
          world_epoch INTEGER NOT NULL DEFAULT 1,
          vector_json TEXT NOT NULL,
          content_hash TEXT NOT NULL,
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS pacing_state_cache (
          branch_id TEXT PRIMARY KEY REFERENCES timeline_branches(branch_id),
          event_id TEXT NOT NULL REFERENCES timeline_events(event_id),
          state_json TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS director_plan_versions (
          version_id INTEGER PRIMARY KEY,
          branch_id TEXT NOT NULL REFERENCES timeline_branches(branch_id),
          source_event_id TEXT NOT NULL REFERENCES timeline_events(event_id),
          plan_json TEXT NOT NULL,
          reflection_reason TEXT NOT NULL,
          changed_story_thread_id INTEGER,
          change_summary TEXT NOT NULL DEFAULT 'no_story_thread_change',
          pace_phase TEXT,
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS director_pulses (
          pulse_id TEXT PRIMARY KEY,
          session_id INTEGER NOT NULL REFERENCES chat_sessions(session_id),
          trigger_event_id TEXT NOT NULL REFERENCES timeline_events(event_id),
          plan_version_id INTEGER NOT NULL DEFAULT 0,
          status TEXT NOT NULL CHECK(status IN ('Pending','Running','Completed','Shadowed','Consumed','Stale','Failed')),
          suggestion_json TEXT,
          baseline_json TEXT,
          is_shadow INTEGER NOT NULL DEFAULT 1 CHECK(is_shadow IN (0,1)),
          benefit_score REAL,
          expires_at TEXT NOT NULL,
          created_at TEXT NOT NULL,
          completed_at TEXT,
          consumed_at TEXT,
          error TEXT
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_director_pulses_session_status
        ON director_pulses(session_id, status);
        """,
        """
        CREATE TABLE IF NOT EXISTS death_return_log (
          log_id INTEGER PRIMARY KEY,
          loop_count INTEGER NOT NULL,
          death_cause TEXT NOT NULL,
          miasma_level INTEGER NOT NULL DEFAULT 0,
          save_point_id INTEGER REFERENCES save_points(save_id),
          chronicle_index TEXT,
          created_at TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS agent_config (
          config_id INTEGER PRIMARY KEY,
          agent_type TEXT NOT NULL CHECK(agent_type IN ('Kepler', 'Director', 'Character', 'RuleResolver', 'Form')),
          agent_name TEXT NOT NULL UNIQUE,
          api_endpoint TEXT NOT NULL,
          api_key TEXT NOT NULL,
          model_name TEXT NOT NULL,
          temperature REAL DEFAULT 0.7 CHECK(temperature >= 0 AND temperature <= 2),
          max_tokens INTEGER DEFAULT 4096 CHECK(max_tokens > 0),
          system_prompt TEXT,
          enabled INTEGER DEFAULT 1 CHECK(enabled IN (0, 1)),
          max_input_tokens INTEGER DEFAULT 4096,
          response_format TEXT DEFAULT 'JSON',
          enable_thinking INTEGER DEFAULT 0,
          reasoning_effort TEXT DEFAULT 'medium',
          auto_retry INTEGER DEFAULT 1
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS protagonist_templates (
          template_id INTEGER PRIMARY KEY,
          template_name TEXT NOT NULL UNIQUE,
          includes_subaru INTEGER DEFAULT 1,
          base_data TEXT NOT NULL,
          is_default INTEGER DEFAULT 0
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS api_routing (
          routing_key TEXT PRIMARY KEY,
          preset_name TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS chat_sessions (
          session_id INTEGER PRIMARY KEY AUTOINCREMENT,
          session_name TEXT NOT NULL,
          is_active INTEGER DEFAULT 0 CHECK(is_active IN (0, 1)),
          created_at TEXT NOT NULL,
          game_mode TEXT NOT NULL CHECK(game_mode IN ('RP', 'Theater')),
          session_time_scale REAL NOT NULL DEFAULT 1.0 CHECK(session_time_scale > 0),
          input_slow_factor REAL NOT NULL DEFAULT 1.0 CHECK(input_slow_factor > 0 AND input_slow_factor <= 1),
          world_clock_anchor TEXT,
          is_paused INTEGER NOT NULL DEFAULT 0 CHECK(is_paused IN (0, 1)),
          current_branch_id TEXT,
          current_world_epoch INTEGER NOT NULL DEFAULT 1
        );
        """
    ];
}
