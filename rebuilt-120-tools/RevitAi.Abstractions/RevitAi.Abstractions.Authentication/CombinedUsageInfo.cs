using System;
using Newtonsoft.Json;

namespace RevitAi.Abstractions.Authentication;

public sealed class CombinedUsageInfo
{
	[JsonProperty("total_balance")]
	public decimal TotalBalance { get; set; }

	[JsonProperty("user_balance")]
	public decimal UserBalance { get; set; }

	[JsonProperty("device_balance")]
	public decimal DeviceBalance { get; set; }

	[JsonProperty("total_granted")]
	public decimal TotalGranted { get; set; }

	[JsonProperty("total_consumed")]
	public decimal TotalConsumed { get; set; }

	[JsonProperty("user_granted")]
	public decimal UserGranted { get; set; }

	[JsonProperty("user_consumed")]
	public decimal UserConsumed { get; set; }

	[JsonProperty("device_granted")]
	public decimal DeviceGranted { get; set; }

	[JsonProperty("device_consumed")]
	public decimal DeviceConsumed { get; set; }

	[JsonProperty("user_id")]
	public Guid? UserId { get; set; }

	[JsonProperty("device_id")]
	public Guid? DeviceId { get; set; }

	[JsonProperty("user_last_recharge_at")]
	public string? UserLastRechargeAt { get; set; }

	[JsonProperty("device_last_recharge_at")]
	public string? DeviceLastRechargeAt { get; set; }

	[JsonProperty("user_last_usage_at")]
	public string? UserLastUsageAt { get; set; }

	[JsonProperty("device_last_usage_at")]
	public string? DeviceLastUsageAt { get; set; }
}
