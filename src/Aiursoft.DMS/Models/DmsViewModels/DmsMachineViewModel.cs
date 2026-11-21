using Aiursoft.DMS.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.DmsViewModels;

public class DmsMachineViewModel : UiStackLayoutViewModel
{
    public required Machine Machine { get; init; }
}
