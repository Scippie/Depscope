using System.Collections.ObjectModel;
using DepScope.Core.Models;

namespace DepScope.Desktop.ViewModels;

public sealed class ProjectGroup
{
    public string Name { get; }
    public string RootPath { get; }

    public ObservableCollection<ProjectInfo> Projects { get; } = new();

    public ProjectGroup(string name, string rootPath)
    {
        Name = name;
        RootPath = rootPath;
    }
}

