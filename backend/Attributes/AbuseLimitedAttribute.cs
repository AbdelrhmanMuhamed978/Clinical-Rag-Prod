namespace ClinicalRag.Api.Attributes;

/// <summary>Marks an expensive authenticated route for per-user rate and concurrency limits.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AbuseLimitedAttribute : Attribute;
