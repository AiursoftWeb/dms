using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.UsersViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Users";
    }

    public required List<UserWithRolesViewModel> Users { get; set; }
}
