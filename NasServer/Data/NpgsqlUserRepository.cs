// NasServer/Data/NpgsqlUserRepository.cs

using System;
using System.Threading;
using System.Threading.Tasks;
using NasServer.Models;
using Npgsql;
using NpgsqlTypes;

namespace NasServer.Data;

/// <summary>
/// PostgreSQL 用户数据仓库实现类
/// </summary>
public sealed class NpgsqlUserRepository(NpgsqlDataSource dataSource) : IUserRepository
{
    /// <summary>
    /// 检查邮箱是否已存在
    /// </summary>
    public async Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
    {
        const string sql = "SELECT 1 FROM \"Users\" WHERE \"Email\" = $1 LIMIT 1";
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = email });
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    /// <summary>
    /// 根据邮箱查找用户
    /// </summary>
    public async Task<User?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        const string sql =
            "SELECT \"Id\", \"Email\", \"PasswordHash\", \"FullName\", \"CreatedAt\" " +
            "FROM \"Users\" WHERE \"Email\" = $1 LIMIT 1";
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = email });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return ReadUser(reader);
    }

    /// <summary>
    /// 添加新用户到数据库
    /// </summary>
    public async Task AddUserAsync(User user, CancellationToken ct = default)
    {
        const string sql =
            "INSERT INTO \"Users\" (\"Id\", \"Email\", \"PasswordHash\", \"FullName\", \"CreatedAt\") " +
            "VALUES ($1, $2, $3, $4, $5)";
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = user.Id });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = user.Email });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = user.PasswordHash });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Varchar,
            Value = (object?)user.FullName ?? DBNull.Value
        });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = user.CreatedAt });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 添加刷新令牌到数据库
    /// </summary>
    public async Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct = default)
    {
        const string sql =
            "INSERT INTO \"RefreshTokens\" " +
            "(\"Id\", \"TokenHash\", \"UserId\", \"ExpiresAt\", \"IsRevoked\", \"CreatedAt\") " +
            "VALUES ($1, $2, $3, $4, $5, $6)";
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = token.Id });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = token.TokenHash });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = token.UserId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = token.ExpiresAt });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = token.IsRevoked });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = token.CreatedAt });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 根据令牌哈希查找刷新令牌及其关联的用户信息
    /// </summary>
    public async Task<(RefreshToken Token, User User)?> FindRefreshTokenWithUserAsync(
        string tokenHash, CancellationToken ct = default)
    {
        const string sql =
            "SELECT r.\"Id\", r.\"TokenHash\", r.\"UserId\", r.\"ExpiresAt\", r.\"IsRevoked\", r.\"CreatedAt\", " +
            "u.\"Id\", u.\"Email\", u.\"PasswordHash\", u.\"FullName\", u.\"CreatedAt\" " +
            "FROM \"RefreshTokens\" r " +
            "JOIN \"Users\" u ON u.\"Id\" = r.\"UserId\" " +
            "WHERE r.\"TokenHash\" = $1 LIMIT 1";
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = tokenHash });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        // 读取刷新令牌信息
        var refreshToken = new RefreshToken
        {
            Id = reader.GetGuid(0),
            TokenHash = reader.GetString(1),
            UserId = reader.GetGuid(2),
            ExpiresAt = reader.GetDateTime(3),
            IsRevoked = reader.GetBoolean(4),
            CreatedAt = reader.GetDateTime(5)
        };
        
        // 读取用户信息
        var user = new User
        {
            Id = reader.GetGuid(6),
            Email = reader.GetString(7),
            PasswordHash = reader.IsDBNull(8) ? [] : reader.GetFieldValue<byte[]>(8),
            FullName = reader.IsDBNull(9) ? null : reader.GetString(9),
            CreatedAt = reader.GetDateTime(10)
        };
        return (refreshToken, user);
    }

    /// <summary>
    /// 撤销指定的刷新令牌
    /// </summary>
    public async Task RevokeRefreshTokenAsync(Guid tokenId, CancellationToken ct = default)
    {
        const string sql = "UPDATE \"RefreshTokens\" SET \"IsRevoked\" = TRUE WHERE \"Id\" = $1";
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = tokenId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 撤销用户的所有有效刷新令牌
    /// </summary>
    public async Task RevokeAllRefreshTokensAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql =
            "UPDATE \"RefreshTokens\" SET \"IsRevoked\" = TRUE " +
            "WHERE \"UserId\" = $1 AND \"IsRevoked\" = FALSE";
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = userId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 从数据读取器中读取用户信息
    /// </summary>
    private static User ReadUser(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Email = reader.GetString(1),
        PasswordHash = reader.IsDBNull(2) ? [] : reader.GetFieldValue<byte[]>(2),
        FullName = reader.IsDBNull(3) ? null : reader.GetString(3),
        CreatedAt = reader.GetDateTime(4)
    };
}
