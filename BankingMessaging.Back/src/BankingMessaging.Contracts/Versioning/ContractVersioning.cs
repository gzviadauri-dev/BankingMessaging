namespace BankingMessaging.Contracts.Versioning;

/// <summary>
/// Documents the contract versioning strategy for all message types in this project.
/// </summary>
/// <remarks>
/// <para><b>Rules for evolving contracts safely (independent producer/consumer deployment):</b></para>
/// <list type="number">
///   <item><b>NEVER remove a property</b> — consumers may still rely on it; old messages contain it.</item>
///   <item><b>NEVER rename a property</b> — Newtonsoft treats it as removed + added; both sides break.</item>
///   <item><b>NEVER add a required (non-nullable, no default) property</b> — old producers won't include it;
///     deserialization produces default(T) silently, which can corrupt business logic.</item>
///   <item><b>ALWAYS add new properties as nullable or with a safe default</b>
///     — ensures old messages deserialize without errors:
///     <code>public string? NewField { get; init; }             // nullable — consumer checks before use
/// public string NewField { get; init; } = "default"; // always safe to read</code>
///   </item>
///   <item><b>For breaking changes, create a V2 record alongside the V1 record.</b>
///     Run both consumers in parallel during the transition window, then retire V1.</item>
/// </list>
///
/// <para><b>Serializer:</b> All services use the MassTransit 8.x default
/// <c>System.Text.Json</c> serializer, which ignores unknown fields during deserialization
/// by default. This enables safe rolling deployment when new fields are added to a contract:
/// old consumers receive a message with an unknown field and simply ignore it.</para>
/// </remarks>
public static class ContractVersioning
{
    public const string CurrentVersion = "v1";
}
