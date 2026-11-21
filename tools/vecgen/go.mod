module github.com/victorcondackdev/ledger-dotnet/tools/vecgen

go 1.22

// The generator runs against a local checkout of the Go implementation,
// cloned next to this repository (../ledger-go relative to the repository root).
replace github.com/victorcondackdev/ledger-go => ../../../ledger-go

require github.com/victorcondackdev/ledger-go v0.0.0-00010101000000-000000000000
