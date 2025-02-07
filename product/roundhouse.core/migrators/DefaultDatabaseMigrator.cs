using System.Collections.Generic;
using System.Linq;
using roundhouse.environments;

namespace roundhouse.migrators
{
    using System;
    using cryptography;
    using databases;
    using infrastructure.app;
    using infrastructure.app.tokens;
    using infrastructure.extensions;
    using infrastructure.logging;
    using sqlsplitters;
    using Environment = roundhouse.environments.Environment;
    using System.Linq;

    public sealed class DefaultDatabaseMigrator : DatabaseMigrator
    {
        public Database database { get; set; }
        private readonly CryptographicService crypto_provider;
        private readonly ConfigurationPropertyHolder configuration;
        private readonly bool restoring_database;
        private readonly string restore_path;
        private readonly string custom_restore_options;
        private readonly string output_path;
        private readonly bool error_on_one_time_script_changes;
        private readonly bool ignore_one_time_script_changes;
        private bool running_in_a_transaction;
        private readonly bool is_running_all_any_time_scripts;
        private readonly bool is_baseline;
        private readonly bool is_dryrun;

        public DefaultDatabaseMigrator(Database database, CryptographicService crypto_provider, ConfigurationPropertyHolder configuration)
        {
            this.database = database;
            this.crypto_provider = crypto_provider;
            this.configuration = configuration;
            restoring_database = configuration.Restore;
            restore_path = configuration.RestoreFromPath;
            custom_restore_options = configuration.RestoreCustomOptions;
            output_path = configuration.OutputPath;
            error_on_one_time_script_changes = !configuration.WarnOnOneTimeScriptChanges && !configuration.WarnAndIgnoreOnOneTimeScriptChanges;
            ignore_one_time_script_changes = configuration.WarnAndIgnoreOnOneTimeScriptChanges;
            is_running_all_any_time_scripts = configuration.RunAllAnyTimeScripts;
            is_baseline = configuration.Baseline;
            is_dryrun = configuration.DryRun;
        }

        public void initialize_connections()
        {
            database.initialize_connections(configuration);
        }

        public void open_admin_connection()
        {
            database.open_admin_connection();
        }

        public void close_admin_connection()
        {
            database.close_admin_connection();
        }

        public void open_connection(bool with_transaction)
        {
            running_in_a_transaction = with_transaction;
            database.open_connection(with_transaction);
        }

        public void close_connection()
        {
            database.close_connection();
        }

        public bool create_or_restore_database(string custom_create_database_script)
        {
            var database_created = false;

            if (string.IsNullOrEmpty(custom_create_database_script))
            {
                Log.bound_to(this).log_an_info_event_containing("Creating {0} database on {1} server if it doesn't exist.", database.database_name, database.server_name);
            }
            else
            {
                Log.bound_to(this).log_an_info_event_containing("Creating {0} database on {1} server with custom script.", database.database_name, database.server_name);
            }

            database_created = database.create_database_if_it_doesnt_exist(custom_create_database_script);

            if (restoring_database)
            {
                database_created = false;
                string custom_script = custom_restore_options;
                if (!configuration.DisableTokenReplacement)
                {
                    custom_script = TokenReplacer.replace_tokens(configuration, custom_script);
                }
                restore_database(restore_path, custom_script);
            }

            return database_created;
        }

        public void backup_database_if_it_exists()
        {
            database.backup_database(output_path);
        }

        public void restore_database(string restore_from_path, string restore_options)
        {
            Log.bound_to(this).log_an_info_event_containing("Restoring {0} database on {1} server from path {2}.", database.database_name, database.server_name, restore_from_path);
            database.restore_database(restore_from_path, restore_options);
        }

        public void set_recovery_mode(bool simple)
        {
            //database.open_connection(false);
            Log.bound_to(this).log_an_info_event_containing("Setting recovery mode to '{0}' for database {1}.", simple ? "Simple" : "Full", database.database_name);
            database.set_recovery_mode(simple);
            //database.close_connection();
        }

        public void run_roundhouse_support_tasks()
        {
            if (running_in_a_transaction)
            {
                database.close_connection();
                database.open_connection(false);
            }

            Log.bound_to(this).log_an_info_event_containing(" Running database type specific tasks.");
            database.run_database_specific_tasks();
            Log.bound_to(this).log_an_info_event_containing(" Creating [{0}] table if it doesn't exist.", database.version_table_name);
            Log.bound_to(this).log_an_info_event_containing(" Creating [{0}] table if it doesn't exist.", database.scripts_run_table_name);
            Log.bound_to(this).log_an_info_event_containing(" Creating [{0}] table if it doesn't exist.", database.scripts_run_errors_table_name);
            database.create_or_update_roundhouse_tables();

            if (running_in_a_transaction)
            {
                database.close_connection();
                database.open_connection(true);
                //transfer_to_database_for_changes();
            }
        }

        public string get_current_version(string repository_path)
        {
            string current_version = database.get_version(repository_path);

            if (string.IsNullOrEmpty(current_version))
            {
                current_version = "0";
            }

            return current_version;
        }

        public void delete_database()
        {
            Log.bound_to(this).log_an_info_event_containing("Deleting {0} database on {1} server if it exists.", database.database_name, database.server_name);
            database.delete_database_if_it_exists();
        }

        public long version_the_database(string repository_path, string repository_version)
        {
            Log.bound_to(this).log_an_info_event_containing(" Versioning {0} database with version {1} based on {2}.", database.database_name, repository_version, repository_path);
            return database.insert_version_and_get_version_id(repository_path, repository_version);
        }

        public bool run_sql(string sql_to_run, string script_name, bool run_this_script_once, bool run_this_script_every_time, long version_id, EnvironmentSet environment_set, string repository_version, string repository_path, ConnectionType connection_type)
        {
            bool this_sql_ran = false;
            bool skip_run = is_baseline;

            if (this_is_a_one_time_script_that_has_changes_but_has_already_been_run(script_name, sql_to_run, run_this_script_once))
            {
                if (error_on_one_time_script_changes)
                {
                    database.rollback();
                    string error_message = string.Format("{0} has changed since the last time it was run. By default this is not allowed - scripts that run once should never change. To change this behavior to a warning, please set warnOnOneTimeScriptChanges to true and run again. Stopping execution.", script_name);
                    record_script_in_scripts_run_errors_table(script_name, sql_to_run, sql_to_run, error_message, repository_version, repository_path);
                    database.close_connection();
                    throw new Exception(error_message);
                }
                if (ignore_one_time_script_changes)
                {
                    skip_run = true;
                }
                Log.bound_to(this).log_a_warning_event_containing("{0} is a one time script that has changed since it was run.", script_name);
            }

            if (this_is_an_environment_file_and_its_in_the_right_environment(script_name, environment_set)
                && this_script_should_run(script_name, sql_to_run, run_this_script_once, run_this_script_every_time))
            {
                if (!is_dryrun)
                {
                    Log.bound_to(this).log_an_info_event_containing(" {3} {0} on {1} - {2}.", script_name, database.server_name, database.database_name,
                                            skip_run ? "BASELINING: Recording" : "Running");
                }
                if (!skip_run)
                {
                    if (!is_dryrun)
                    {
                        foreach (var sql_statement in get_statements_to_run(sql_to_run))
                        {
                            try
                            {
                                database.run_sql(sql_statement, connection_type);
                            }
                            catch (Exception ex)
                            {
                                database.rollback();

                                record_script_in_scripts_run_errors_table(script_name, sql_to_run, sql_statement, ex.Message, repository_version, repository_path);
                                database.close_connection();
                                throw;
                            }
                        }
                    }
                    else
                    {
                        Log.bound_to(this).log_a_warning_event_containing(" DryRun: {0} on {1} - {2}.", script_name, database.server_name, database.database_name);
                    }
                }
                if (!is_dryrun)
                {
                    record_script_in_scripts_run_table(script_name, sql_to_run, run_this_script_once, version_id);
                    this_sql_ran = true;
                }
            }
            else
            {
                Log.bound_to(this).log_an_info_event_containing(" Skipped {0} - {1}.", script_name, run_this_script_once ? "One time script" : "No changes were found to run");
            }

            return this_sql_ran;
        }

        public IEnumerable<string> get_statements_to_run(string sql_to_run)
        {
            IList<string> sql_statements = new List<string>();

            if (database.split_batch_statements)
            {
                foreach (var sql_statement in StatementSplitter.split_sql_on_regex_and_remove_empty_statements(sql_to_run, database.sql_statement_separator_regex_pattern))
                {
                    sql_statements.Add(sql_statement);
                }
            }
            else
            {
                sql_statements.Add(sql_to_run);
            }

            return sql_statements;
        }

        public void record_script_in_scripts_run_table(string script_name, string sql_to_run, bool run_this_script_once, long version_id)
        {
            Log.bound_to(this).log_a_debug_event_containing("Recording {0} script ran on {1} - {2}.", script_name, database.server_name, database.database_name);
            database.insert_script_run(script_name, sql_to_run, create_hash(sql_to_run, true), run_this_script_once, version_id);
        }

        public void record_script_in_scripts_run_errors_table(string script_name, string sql_to_run, string sql_erroneous_part, string error_message, string repository_version, string repository_path)
        {
            Log.bound_to(this).log_a_debug_event_containing("Recording {0} script ran with error on {1} - {2}.", script_name, database.server_name, database.database_name);
            database.insert_script_run_error(script_name, sql_to_run, sql_erroneous_part, error_message, repository_version, repository_path);
        }

        private string create_hash(string sql_to_run, bool normalizeEndings)
        {
            var input = sql_to_run.Replace(@"'", @"''");
            if (normalizeEndings)
                input = input.Replace(WindowsLineEnding, UnixLineEnding).Replace(MacLineEnding, UnixLineEnding);
            return crypto_provider.hash(input);
        }

        public bool this_is_an_every_time_script(string script_name, bool run_this_script_every_time)
        {
            bool this_is_an_everytime_script = false;

            if (run_this_script_every_time)
            {
                this_is_an_everytime_script = true;
            }

            if (script_name.to_lower().StartsWith("everytime."))
            {
                this_is_an_everytime_script = true;
            }

            if (script_name.to_lower().Contains(".everytime."))
            {
                this_is_an_everytime_script = true;
            }

            return this_is_an_everytime_script;
        }

        private bool this_script_has_run_already(string script_name)
        {
            return database.has_run_script_already(script_name);
        }

        private bool this_is_a_one_time_script_that_has_changes_but_has_already_been_run(string script_name, string sql_to_run, bool run_this_script_once)
        {
            return this_script_has_changed_since_last_run(script_name, sql_to_run) && this_script_has_run_already(script_name) && run_this_script_once;
        }

        private bool this_script_has_changed_since_last_run(string script_name, string sql_to_run)
        {
            string old_text_hash = string.Empty;
            try
            {
                old_text_hash = database.get_current_script_hash(script_name);
            }
            catch (Exception ex)
            {
                Log.bound_to(this).log_a_warning_event_containing("{0} - I didn't find this script executed before.{1}{2}", script_name, System.Environment.NewLine, ex.to_string());
            }

            if (string.IsNullOrEmpty(old_text_hash)) return true;


            // These check hashes from before the normalization change and after
            // The change does result in a different hash that will not be the result of
            // any sore of file change and therefore should not be logged.
            bool hash_is_same = 
                hashes_are_equal(create_hash(sql_to_run, true), old_text_hash) ||   // New hash
                hashes_are_equal(create_hash(sql_to_run, false), old_text_hash);    // Old hash

            if (!hash_is_same)
            {
                // extra checks if only line endings have changed
                hash_is_same = have_same_hash_ignoring_platform(sql_to_run, old_text_hash);
                if (hash_is_same)
                {
                    Log.bound_to(this).log_a_warning_event_containing("Script {0} had different line endings than before but equal content", script_name);
                }
            }

            return !hash_is_same;
        }

        private bool hashes_are_equal(string new_text_hash, string old_text_hash)
        {
            return string.Compare(old_text_hash, new_text_hash, true) == 0;
        }

        private const string WindowsLineEnding = "\r\n";
        private const string UnixLineEnding = "\n";
        private const string MacLineEnding = "\r";

        private bool have_same_hash_ignoring_platform(string sql_to_run, string old_text_hash)
        {
            var lineEndingVariations = new List<string> {WindowsLineEnding, UnixLineEnding, MacLineEnding};

            return lineEndingVariations.Any(variation => {
                var normalized_sql = lineEndingVariations.Aggregate(sql_to_run, (norm, ending) => norm.Replace(ending, variation));
                return hashes_are_equal(create_hash(normalized_sql, false), old_text_hash);
            });
        }

        private bool this_script_should_run(string script_name, string sql_to_run, bool run_this_script_once, bool run_this_script_every_time)
        {
            if (this_is_an_every_time_script(script_name, run_this_script_every_time))
            {
                return true;
            }

            if (is_running_all_any_time_scripts && !run_this_script_once)
            {
                return true;
            }

            if (this_script_has_run_already(script_name)
                && !this_script_has_changed_since_last_run(script_name, sql_to_run))
            {
                return false;
            }

            return true;
        }

        public bool this_script_is_new_or_updated(string script_name, string sql_to_run, EnvironmentSet environment_set)
        {
            if (!this_is_an_environment_file_and_its_in_the_right_environment(script_name, environment_set))
                return false;

            if (this_script_has_run_already(script_name)
                   && !this_script_has_changed_since_last_run(script_name, sql_to_run))
                return false;

            return true;
        }

        public bool this_is_an_environment_file_and_its_in_the_right_environment(string script_name, EnvironmentSet environment_set)
        {
            string environment_set_names = string.Join(", ", environment_set.set_items.Select(x => x.name));

            Log.bound_to(this).log_a_debug_event_containing("Checking to see if {0} is an environment file. We have an environment set containing: .", script_name, environment_set_names);

            if (!script_name.to_lower().Contains(".env."))
            {
                // return true because this is NOT an environment file for the next check
                return true;
            }

            bool environment_file_is_in_the_right_environment = environment_set.item_is_for_this_environment_set(script_name);

            Log.bound_to(this).log_an_info_event_containing(" {0} is an environment file. We have an environment set containing: {1}. This will{2} run based on this check.",
                                                            script_name, environment_set_names, environment_file_is_in_the_right_environment ? string.Empty : " NOT");

            return environment_file_is_in_the_right_environment;
        }
    }
}