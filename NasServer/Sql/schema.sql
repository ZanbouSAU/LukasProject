/* NasServer/Sql/schema.sql
 *
 * NasServer 数据库结构。数据库只承担账号体系（用户 + 刷新令牌），
 * 文件数据全部落在文件系统（/var/opt/cloudstorage/users/<user_id>），因此结构刻意保持简单。
 *
 * 初始化示例（以 postgres 超级用户执行）：
 *
 *   CREATE ROLE userdata LOGIN PASSWORD '<请替换为强密码>';
 *   CREATE DATABASE "UserData" OWNER userdata;
 *   \c "UserData"
 *   -- 然后以 userdata 身份执行本文件：
 *   --   psql -h localhost -U userdata -d UserData -f schema.sql
 */

DROP TABLE IF EXISTS "RefreshTokens";
DROP TABLE IF EXISTS "Users";

/* ---------------------------------------------------------------------------
 * 用户表。
 * Email 业务上不区分大小写：应用层已统一 trim+小写后存取，
 * 这里再加 lower("Email") 唯一索引兜底，防止绕过应用层直接写库造成重复账号。
 * PasswordHash 存 BCrypt 哈希字节。
 * ------------------------------------------------------------------------- */
CREATE TABLE IF NOT EXISTS "Users" (
    "Id"           uuid                     NOT NULL,
    "Email"        character varying(254)   NOT NULL,
    "PasswordHash" bytea                    NOT NULL,
    "FullName"     character varying(50)    NULL,
    "CreatedAt"    timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_Users" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Email_Lower" ON "Users" (lower("Email"));

/* ---------------------------------------------------------------------------
 * 刷新令牌表。
 * TokenHash 存令牌的 SHA-256（Base64，44 字符）——数据库被拖走也无法直接冒用令牌；
 * 原始令牌只在签发响应中出现一次。
 * 令牌旋转：refresh 成功后旧令牌置 IsRevoked，签发新令牌；
 * 已吊销令牌被再次使用视为令牌泄露，应用层会吊销该用户全部令牌。
 * ------------------------------------------------------------------------- */
CREATE TABLE IF NOT EXISTS "RefreshTokens" (
    "Id"        uuid                     NOT NULL,
    "TokenHash" character varying(64)    NOT NULL,
    "UserId"    uuid                     NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "IsRevoked" boolean                  NOT NULL DEFAULT FALSE,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
    CONSTRAINT "PK_RefreshTokens" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_RefreshTokens_Users_UserId"
        FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_RefreshTokens_TokenHash" ON "RefreshTokens" ("TokenHash");
CREATE INDEX IF NOT EXISTS "IX_RefreshTokens_UserId" ON "RefreshTokens" ("UserId");

/* ---------------------------------------------------------------------------
 * 维护建议：刷新令牌默认 7 天过期，过期/吊销的行可定期清理，例如配置 cron：
 *
 *   DELETE FROM "RefreshTokens"
 *   WHERE "ExpiresAt" < now() - interval '30 days';
 * ------------------------------------------------------------------------- */
