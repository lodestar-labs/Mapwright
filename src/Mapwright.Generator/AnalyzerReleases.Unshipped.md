; Unshipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
MW0001  | Mapwright | Warning | Destination property is not mapped
MW0002  | Mapwright | Info | Source property is never read
MW0003  | Mapwright | Error | Mapping configuration names an unknown property
MW0004  | Mapwright | Error | No conversion between mapped properties
MW0005  | Mapwright | Error | Unsupported mapping method signature
MW0006  | Mapwright | Warning | Init-only property cannot be set by an in-place copy
MW0007  | Mapwright | Error | Projection would recurse forever
MW0008  | Mapwright | Error | Collection map has no element map
MW0009  | Mapwright | Error | AfterMap method is invalid
