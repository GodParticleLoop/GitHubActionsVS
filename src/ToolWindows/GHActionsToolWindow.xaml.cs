﻿using GitHubActionsVS.Helpers;
using GitHubActionsVS.Models;
using GitHubActionsVS.ToolWindows;
using Octokit;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitHubActionsVS.UserControls;
using Application = System.Windows.Application;
using System.Windows.Media;
using MessageBox = Community.VisualStudio.Toolkit.MessageBox;

namespace GitHubActionsVS;

/// <summary>
/// Interaction logic for GHActionsToolWindow.xaml
/// </summary>
public partial class GHActionsToolWindow : UserControl
{
    private readonly RepoInfo _repoInfo = null;
    private readonly ToolWindowMessenger _toolWindowMessenger = null;

    public GHActionsToolWindow(ToolWindowMessenger toolWindowMessenger)
    {
        _toolWindowMessenger = toolWindowMessenger ??= new();
        toolWindowMessenger.MessageReceived += OnMessageReceived;
        InitializeComponent();
        _repoInfo = new();

        _ = GetRepoInfoAsync();
    }

    private void OnMessageReceived(object sender, MessagePayload payload)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var (command, text) = payload;
            await (command switch
            {
                MessageCommand.Refresh => GetRepoInfoAsync(),
                MessageCommand.GotoRepo => GotoRepoAsync(),

                _ => Task.CompletedTask
            });
        }).FireAndForget();
    }

    private Task GotoRepoAsync()
    {
        if (_repoInfo is { RepoUrl.Length: > 0 })
        {
            Process.Start($"{_repoInfo?.RepoUrl}/actions");
        }

        return Task.CompletedTask;
    }

    public void ResetTrees() => ClearTreeViews();

    public async Task GetRepoInfoAsync()
    {
        ClearTreeViews();
        _repoInfo.RepoOwner = null;
        _repoInfo.RepoName = null;
        _repoInfo.IsGitHub = false;
        _repoInfo.RepoUrl = null;

        // find the git folder
        var solution = await VS.Solutions.GetCurrentSolutionAsync();
        if (solution is null)
        {
            Debug.WriteLine("No solution found");
            ShowInfoMessage("No project or solution loaded");
            return;
        }
        var projectPath = solution?.FullPath;

        _repoInfo.FindGitFolder(projectPath, out string gitPath);

        if (string.IsNullOrWhiteSpace(gitPath))
        {
            Debug.WriteLine("No git repo found");
            ShowInfoMessage("No git repo found");
        }
        else
        {
            Debug.WriteLine($"Found git repo at {gitPath}");
            if (_repoInfo.IsGitHub)
            {
                Debug.WriteLine($"GitHub repo: {_repoInfo.RepoOwner}/{_repoInfo.RepoName}");
                await LoadDataAsync();
            }
            else
            {
                Debug.WriteLine("Not a GitHub repo");
                ShowInfoMessage("Repo found, but not a github.com");
            }
        }
    }

    private void ShowInfoMessage(string messageString)
    {
        ThreadHelper.JoinableTaskFactory.Run(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            MessageArea.Text = messageString;
            MessageArea.Visibility = Visibility.Visible;
            ActionsInfoPanel.Visibility = Visibility.Collapsed;
        });
    }

    private void ClearTreeViews()
    {
        tvSecrets.ItemsSource = null;
        tvSecrets.Header = "Repository Secrets";
        tvEnvironments.ItemsSource = null;
        tvEnvironments.Header = "Environments";
        tvCurrentBranch.ItemsSource = null;
        CurrentBranchExpander.IsExpanded = false;
    }

    private async Task LoadDataAsync()
    {
        MessageArea.Visibility = Visibility.Collapsed;
        ActionsInfoPanel.Visibility = Visibility.Visible;

        // get the settings
        var generalSettings = await ExtensionOptions.GetLiveInstanceAsync();
        var maxRuns = generalSettings.MaxRuns;

        refreshProgress.IsIndeterminate = true;
        refreshProgress.Visibility = Visibility.Visible;

        GitHubClient client = GetGitHubClient();

        try
        {
            // get secrets
            await RefreshSecretsAsync(client);
            // get environments
            await RefreshEnvironmentsAsync(client);

            // get current branch
            var runs = await client.Actions?.Workflows?.Runs?.List(_repoInfo.RepoOwner, _repoInfo.RepoName, new WorkflowRunsRequest() { Branch = _repoInfo.CurrentBranch }, new ApiOptions() { PageCount = 1, PageSize = maxRuns });

            List<SimpleRun> runsList = new List<SimpleRun>();

            if (runs.TotalCount > 0)
            {
                // creating simplified model of the GH info for the treeview

                // iterate throught the runs
                foreach (var run in runs.WorkflowRuns)
                {
                    SimpleRun simpleRun = new()
                    {
                        Conclusion = run.Conclusion is not null ? run.Conclusion.Value.StringValue : run.Status.StringValue,
                        Name = run.Name,
                        LogDate = run.UpdatedAt,
                        Id = run.Id.ToString(),
                        RunNumber = run.RunNumber.ToString()
                    };

                    // get the jobs for the run
                    var jobs = await client.Actions.Workflows.Jobs?.List(_repoInfo.RepoOwner, _repoInfo.RepoName, run.Id);

                    List<SimpleJob> simpleJobs = new();

                    // iterate through the jobs' steps
                    foreach (var job in jobs.Jobs)
                    {
                        List<SimpleJob> steps = new();
                        foreach (var step in job.Steps)
                        {
                            steps.Add(new SimpleJob()
                            {
                                Conclusion = step.Conclusion is not null ? step.Conclusion.Value.StringValue : step.Status.StringValue,
                                Name = step.Name,
                                Url = $"{job.HtmlUrl}#step:{step.Number.ToString()}:1"
                            });
                        }
                        simpleJobs.Add(new SimpleJob()
                        {
                            Conclusion = job.Conclusion is not null ? job.Conclusion.Value.StringValue : job.Status.StringValue,
                            Name = job.Name,
                            Id = job.Id.ToString(),
                            Jobs = steps // add the steps to the job
                        });
                    }

                    // add the jobs to the run
                    simpleRun.Jobs = simpleJobs;

                    runsList.Add(simpleRun);
                }
            }
            else
            {
                // no runs found
                var noRunsItem = new SimpleRun
                {
                    Name = "No workflow runs found for query",
                    Conclusion = "warning",
                    LogDate = DateTime.Now,
                    RunNumber = "N/A"
                };
                runsList.Add(noRunsItem);
            }

            tvCurrentBranch.ItemsSource = runsList;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }

        CurrentBranchExpander.IsExpanded = true;
        refreshProgress.Visibility = Visibility.Collapsed;
        refreshProgress.IsIndeterminate = false;
    }

    private async Task RefreshEnvironmentsAsync(GitHubClient client)
    {
        var repoEnvs = await client.Repository?.Environment?.GetAll(_repoInfo.RepoOwner, _repoInfo.RepoName);
        List<SimpleEnvironment> envList = new List<SimpleEnvironment>();
        if (repoEnvs.TotalCount > 0)
        {
            tvEnvironments.Header = $"Environments ({repoEnvs.TotalCount})";
            foreach (var env in repoEnvs.Environments)
            {
                var envItem = new SimpleEnvironment
                {
                    Name = env.Name,
                    Url = env.HtmlUrl
                };

                envList.Add(envItem);
            }
        }
        else
        {
            envList.Add(new() {  Name = "No environments defined"});
        }

        tvEnvironments.ItemsSource = envList;
    }

    private async Task RefreshSecretsAsync(GitHubClient client)
    {
        var repoSecrets = await client.Repository?.Actions?.Secrets?.GetAll(_repoInfo.RepoOwner, _repoInfo.RepoName);
        List<string> secretList = new();
        if (repoSecrets.TotalCount > 0)
        {
            tvSecrets.Header = $"Repository Secrets ({repoSecrets.TotalCount})";
            foreach (var secret in repoSecrets.Secrets)
            {
                var updatedOrCreatedAt = secret.UpdatedAt.GetValueOrDefault(secret.CreatedAt);
                secretList.Add($"{secret.Name} ({updatedOrCreatedAt:g})");
            }
        }
        else
        {
            tvSecrets.Header = $"Repository Secrets";
            secretList.Add("No repository secrets defined");
        }
        tvSecrets.ItemsSource = secretList;
    }

    private static GitHubClient GetGitHubClient()
    {
        var creds = CredentialManager.GetCredentials("git:https://github.com");
        var client = new GitHubClient(new ProductHeaderValue("VisualStudio"))
        {
            Credentials = new(creds.Username, creds.Password)
        };

        return client;
    }

    private void JobItem_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // get the items Tag
        if (sender is TreeViewItem item && item.Header is SimpleJob job && job.Url is not null)
        {
            Process.Start(job.Url);
        }
    }

    private void HandlePreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!e.Handled)
        {
            e.Handled = true;
            var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta);
            eventArg.RoutedEvent = UIElement.MouseWheelEvent;
            eventArg.Source = sender;
            var parent = ((Control)sender).Parent as UIElement;
            parent.RaiseEvent(eventArg);
        }
    }

    private async void AddSecret_Click(object sender, RoutedEventArgs e)
    {
        await UpsertRepositorySecret(string.Empty);
    }

    private async void EditSecret_Click(object sender, RoutedEventArgs e)
    {
        MenuItem menuItem = (MenuItem)sender;
        TextBlock tvi = GetParentTreeViewItem(menuItem);
        if (tvi is not null)
        {
            string header = tvi.Text.ToString();
            string secretName = header.Substring(0, header.IndexOf(" ("));
            await UpsertRepositorySecret(secretName);
        }
    }

    private TextBlock GetParentTreeViewItem(MenuItem menuItem)
    {
        var contextMenu = menuItem.CommandParameter as ContextMenu;
        if (contextMenu is not null)
        {
            var treeViewItem = contextMenu.PlacementTarget as TextBlock;
            if (treeViewItem is not null)
            {
                return treeViewItem;
            }
        }
        return null;
    }

    private async void DeleteSecret_Click(object sender, RoutedEventArgs e)
    {
        MenuItem menuItem = (MenuItem)sender;
        TextBlock tvi = GetParentTreeViewItem(menuItem);

        if (tvi is not null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // confirm the delete first
            int result = VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, "Are you sure you want to delete this secret?", "Confirm Delete", Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_QUERY, Microsoft.VisualStudio.Shell.Interop.OLEMSGBUTTON.OLEMSGBUTTON_YESNOCANCEL, Microsoft.VisualStudio.Shell.Interop.OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_THIRD);

            var confirmResult = (MessageBoxResult)result;

            if (confirmResult == MessageBoxResult.Yes)
            {
                string header = tvi.Text.ToString();
                string secretName = header.Substring(0, header.IndexOf(" ("));

                GitHubClient client = GetGitHubClient();
                await client.Repository.Actions.Secrets.Delete(_repoInfo.RepoOwner, _repoInfo.RepoName, secretName);
                await RefreshSecretsAsync(client);
            }
        }
    }

    private async Task UpsertRepositorySecret(string secretName)
    {
        AddEditSecret addEditSecret = new AddEditSecret(secretName)
        {
            Owner = Application.Current.MainWindow
        };
        bool? result = addEditSecret.ShowDialog();
        if (result == true)
        {
            GitHubClient client = GetGitHubClient();
            var pubKey = await client.Repository.Actions.Secrets.GetPublicKey(_repoInfo.RepoOwner, _repoInfo.RepoName);

            UpsertRepositorySecret encryptedSecret = new UpsertRepositorySecret();
            if (pubKey != null)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(addEditSecret.SecretValue);
                var key = Convert.FromBase64String(pubKey.Key);
                var sealedKeyBox = Sodium.SealedPublicKeyBox.Create(bytes, key);
                encryptedSecret.KeyId = pubKey.KeyId;
                encryptedSecret.EncryptedValue = Convert.ToBase64String(sealedKeyBox);
                _ = await client.Repository.Actions.Secrets.CreateOrUpdate(_repoInfo.RepoOwner, _repoInfo.RepoName, addEditSecret.SecretName, encryptedSecret);
            }
            await RefreshSecretsAsync(client);
        }
    }
}

