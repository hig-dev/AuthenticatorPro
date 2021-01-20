﻿using System;
using System.IO;
using System.Threading.Tasks;
using Android.Content;
using AndroidX.Preference;
using SQLite;
using Xamarin.Essentials;

namespace AuthenticatorPro.Data
{
    internal static class Database
    {
        private const string FileName = "proauth.db3";
        private static SQLiteAsyncConnection _sharedConnection;

        public static async Task<SQLiteAsyncConnection> GetSharedConnection()
        {
            if(_sharedConnection == null)
                throw new Exception("Shared connection not open");

            try
            {
                // Attempt to use the connection
                await _sharedConnection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_master");
            }
            catch
            {
                await _sharedConnection.CloseAsync();
                _sharedConnection = null;
                throw;
            }

            return _sharedConnection;
        }

        public static async Task OpenSharedConnection(string password)
        {
            await CloseSharedConnection();
            _sharedConnection = await GetPrivateConnection(password);
        }

        public static async Task CloseSharedConnection()
        {
            if(_sharedConnection == null)
                return;
                
            await _sharedConnection.CloseAsync();
            _sharedConnection = null;
        }

        public static async Task<SQLiteAsyncConnection> GetPrivateConnection(string password)
        {
            var dbPath = GetPath();
            SQLiteAsyncConnection connection;

            if(!String.IsNullOrEmpty(password))
            {
                var connStr = new SQLiteConnectionString(dbPath, true, password);
                connection = new SQLiteAsyncConnection(connStr);
            }
            else
                connection = new SQLiteAsyncConnection(dbPath);

            try
            {
                await connection.EnableWriteAheadLoggingAsync();
                await connection.CreateTableAsync<Authenticator>();
                await connection.CreateTableAsync<Category>();
                await connection.CreateTableAsync<AuthenticatorCategory>();
                await connection.CreateTableAsync<CustomIcon>();
            }
            catch
            {
                await connection.CloseAsync();
                throw;
            }

            return connection;
        }

        private static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                FileName
            );
        }

        public static async Task SetPassword(string currentPassword, string newPassword)
        {
            var dbPath = GetPath();
            var backupPath = dbPath + ".backup";
                
            void DeleteDatabase()
            {
                File.Delete(dbPath);
                File.Delete(dbPath.Replace("db3", "db3-shm"));
                File.Delete(dbPath.Replace("db3", "db3-wal"));
            }
            
            void RestoreBackup()
            {
                DeleteDatabase();
                File.Move(backupPath, dbPath);
            }
            
            File.Copy(dbPath, backupPath);
            var conn = await GetPrivateConnection(currentPassword);

            // Change encryption mode
            if(currentPassword == null && newPassword != null || currentPassword != null && newPassword == null)
            {
                var tempPath = dbPath + ".temp";

                try
                {
                    if(newPassword != null)
                        await conn.ExecuteAsync("ATTACH DATABASE ? AS temporary KEY ?", tempPath, newPassword);
                    else
                        await conn.ExecuteAsync("ATTACH DATABASE ? AS temporary KEY ''", tempPath);

                    await conn.ExecuteScalarAsync<string>("SELECT sqlcipher_export('temporary')");
                }
                catch
                {
                    File.Delete(tempPath);
                    File.Delete(backupPath);
                    throw;
                }
                finally
                {
                    await conn.ExecuteAsync("DETACH DATABASE temporary");
                    await conn.CloseAsync();
                }
                
                try
                {
                    DeleteDatabase();
                    File.Move(tempPath, dbPath);
                    conn = await GetPrivateConnection(newPassword);
                }
                catch
                {
                    // Perhaps it wasn't moved correctly
                    File.Delete(tempPath);
                    RestoreBackup();
                    throw;
                }
                finally
                {
                    await conn.CloseAsync();
                    File.Delete(backupPath);
                }
            }
            // Change password
            else
            {
                // Cannot use parameters with pragma https://github.com/ericsink/SQLitePCL.raw/issues/153
                var quoted = "'" + newPassword.Replace("'", "''") + "'";

                try
                {
                    try
                    {
                        await conn.ExecuteAsync($"PRAGMA rekey = {quoted}");
                    }
                    finally
                    {
                        await conn.CloseAsync();
                    }

                    try
                    {
                        conn = await GetPrivateConnection(newPassword);
                    }
                    finally
                    {
                        await conn.CloseAsync();
                    }
                }
                catch
                {
                    RestoreBackup();
                    throw;
                }
                finally
                {
                    File.Delete(backupPath);
                }
            }
        }

        public static async Task UpgradeLegacy(Context context)
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(context);
            var oldPref = prefs.GetBoolean("pref_useEncryptedDatabase", false);
            
            if(!oldPref)
                return;
                   
            string key = null;
            
            await Task.Run(async delegate
            {
                key = await SecureStorage.GetAsync("database_key");
            });

            // this shouldn't happen
            if(key == null)
                return;

            await SetPassword(key, null);
            prefs.Edit().PutBoolean("pref_useEncryptedDatabase", false).Commit();
        }
    }
}