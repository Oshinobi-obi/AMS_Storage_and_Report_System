namespace AMS_Storage_and_Report_System.Components.Shared;

/// One choice in a <Picker>. Detail is shown as a second, smaller line (e.g. a position).
public sealed record PickOption<TValue>(TValue Value, string Label, string? Detail = null);
