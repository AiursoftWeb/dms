using Aiursoft.DMS.Contracts;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.DmsViewModels;

public enum WorkflowDefinitionSort
{
    Name,
    NameDescending,
    RiskDescending,
    RiskAscending,
    Location
}

public class DmsDefinitionsViewModel : UiStackLayoutViewModel
{
    public required WorkflowCatalogResult Catalog { get; init; }
    public WorkflowDefinitionSort Sort { get; init; }
}
