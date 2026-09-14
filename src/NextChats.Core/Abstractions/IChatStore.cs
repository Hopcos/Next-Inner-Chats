using NextChats.Core.Domain;
using NextChats.Core.Entities;

namespace NextChats.Core.Abstractions;

/// <summary>会话/消息/审批/用量/幂等存储（按用户隔离）</summary>
public interface IChatStore
{
    // ---------- 会话 ----------
    Task<ChatSession> CreateSessionAsync(Guid userId, string title, CancellationToken ct = default);

    Task<ChatSession?> GetSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<ChatSession>> ListSessionsAsync(Guid userId, CancellationToken ct = default);

    Task UpdateSessionAsync(ChatSession session, CancellationToken ct = default);

    /// <summary>重命名会话（仅本人）</summary>
    Task<bool> RenameSessionAsync(Guid userId, Guid sessionId, string title, CancellationToken ct = default);

    /// <summary>置顶/取消置顶会话（仅本人；置顶成功返回 true）</summary>
    Task<bool> SetSessionPinnedAsync(Guid userId, Guid sessionId, bool pinned, CancellationToken ct = default);

    Task DeleteSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(Guid userId, Guid sessionId, CancellationToken ct = default);

    /// <summary>追加消息（幂等：同一 ClientMessageId 只落一条）</summary>
    Task<ChatMessage> AppendMessageAsync(ChatMessage message, CancellationToken ct = default);

    /// <summary>删除指定消息及其之后的所有消息（截断会话到该消息之前，含该条）</summary>
    Task<bool> TruncateFromMessageAsync(Guid userId, Guid sessionId, Guid messageId, CancellationToken ct = default);

    // ---------- 审批 ----------
    Task<ToolApproval> CreateApprovalAsync(ToolApproval approval, CancellationToken ct = default);

    Task<ToolApproval?> GetApprovalAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<ToolApproval>> ListApprovalsAsync(Guid? userId, ApprovalStatus? status, int take, CancellationToken ct = default);

    Task<ToolApproval> UpdateApprovalAsync(ToolApproval approval, CancellationToken ct = default);

    // ---------- 用量/可观测 ----------
    Task RecordUsageAsync(TokenUsageRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<TokenUsageRecord>> QueryUsageAsync(Guid? userId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct = default);

    // ---------- 幂等 ----------
    Task<IdempotencyRecord?> GetIdempotencyAsync(Guid userId, string key, CancellationToken ct = default);

    Task StoreIdempotencyAsync(Guid userId, string key, string responseJson, CancellationToken ct = default);

    // ---------- 用户收藏 ----------
    Task<IReadOnlyList<UserFavorite>> ListFavoritesAsync(Guid userId, CancellationToken ct = default);

    Task<UserFavorite?> FindFavoriteByQuestionAsync(Guid userId, Guid? questionMessageId, CancellationToken ct = default);

    Task<UserFavorite> AddFavoriteAsync(UserFavorite favorite, CancellationToken ct = default);

    Task<bool> RenameFavoriteAsync(Guid userId, Guid id, string title, CancellationToken ct = default);

    Task<bool> DeleteFavoriteAsync(Guid userId, Guid id, CancellationToken ct = default);
}
