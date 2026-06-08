using Xunit;

namespace QuickMail.Tests;

// Marks all WPF window-loading test classes as belonging to a single collection,
// which prevents xUnit from running them in parallel. Parallel XAML InitializeComponent()
// calls race inside PackagePart.CleanUpRequestedStreamsList() causing flaky failures.
[CollectionDefinition("WpfTests")]
public class WpfTestsCollection { }
