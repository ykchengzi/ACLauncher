using System.Collections.ObjectModel;
using System.Diagnostics;
using ACLauncher.Models;
using ACLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace ACLauncher.ViewModels;

/// <summary>
/// 账户页视图模型：账户列表、添加（Microsoft / 离线）、切换、删除。
/// </summary>
public class AccountViewModel : ViewModelBase
{
    private readonly AuthService _authService = AuthService.Instance;
    private readonly DispatcherQueue _dispatcher;

    /// <summary>账户列表。</summary>
    public ObservableCollection<Account> Accounts { get; } = new();

    private Account? _selectedAccount;

    /// <summary>当前选中的账户（默认选中当前账户）。</summary>
    public Account? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
            {
                OnPropertyChanged(nameof(CanSwitch));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(CanRelogin));
            }
        }
    }

    /// <summary>当前生效账户。</summary>
    public Account? CurrentAccount => _authService.CurrentAccount;

    /// <summary>是否可切换（选中了非当前账户）。</summary>
    public bool CanSwitch => SelectedAccount is not null && SelectedAccount != _authService.CurrentAccount;

    /// <summary>是否可删除。</summary>
    public bool CanDelete => SelectedAccount is not null;

    /// <summary>是否可重新登录（仅外置登录账户）。</summary>
    public bool CanRelogin => SelectedAccount?.Type == AccountType.Yggdrasil;

    public AccountViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        // 任何账户变化（登录 / 切换 / 删除）都刷新列表
        _authService.AccountsChanged += () => _dispatcher.TryEnqueue(Refresh);
    }

    /// <summary>初始化：从本地加载账户。</summary>
    public async Task InitializeAsync()
    {
        await _authService.LoadAsync();
        Refresh();
    }

    /// <summary>
    /// 添加 Microsoft 账户（设备码登录）。
    /// 弹出对话框展示设备码与授权链接，自动复制设备码，
    /// 3 秒后自动打开授权网页；轮询期间可取消。
    /// </summary>
    public async Task AddMicrosoftAccountAsync(XamlRoot xamlRoot)
    {
        try
        {
            var codeText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "正在请求设备代码...",
            };
            // 红色醒目提示（获得设备码后显示）
            var hintText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "",
                Visibility = Visibility.Collapsed,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.IndianRed),
            };
            var dialog = new ContentDialog
            {
                Title = "Microsoft 账户登录",
                CloseButtonText = "取消",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children = { codeText, hintText },
                },
                XamlRoot = xamlRoot,
            };

            using var cts = new CancellationTokenSource();
            var dialogTask = dialog.ShowAsync();
            dialog.Closed += (_, _) => cts.Cancel();

            try
            {
                var account = await _authService.LoginMicrosoftAsync(userCode =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        codeText.Text =
                            "请在浏览器打开以下链接并输入设备代码：\n\n" +
                            "https://www.microsoft.com/link\n\n" +
                            $"设备代码：{userCode}\n\n" +
                            "正在等待授权...";

                        // 1. 复制设备码到剪贴板
                        try
                        {
                            var pkg = new DataPackage();
                            pkg.SetText(userCode);
                            Clipboard.SetContent(pkg);
                        }
                        catch
                        {
                            // 剪贴板不可用时忽略，用户可手动输入
                        }

                        // 2. 红字提示
                        hintText.Text =
                            "已复制设备码，3 秒后将打开网页，输入设备码登录完毕后返回启动器即可登录成功。";
                        hintText.Visibility = Visibility.Visible;

                        // 3. 3 秒后自动打开授权网页
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(3000);
                            if (cts.IsCancellationRequested) return;
                            try
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = "https://www.microsoft.com/link",
                                    UseShellExecute = true,
                                });
                            }
                            catch
                            {
                                // 打开浏览器失败时忽略，用户可手动访问
                            }
                        });
                    });
                }, cts.Token);

                TryHideDialog(dialog);
                await _authService.SaveAsync();
                Refresh();
            }
            catch (OperationCanceledException)
            {
                // 用户点击"取消"
            }
            catch (Exception ex)
            {
                TryHideDialog(dialog);
                await ShowErrorAsync(xamlRoot, ex.Message);
            }
            finally
            {
                try { await dialogTask; } catch { /* 对话框已被关闭 */ }
            }
        }
        catch (Exception ex)
        {
            // 最外层保护：任何异常都不允许闪退
            LogService.Instance.Log($"添加 Microsoft 账户失败：{ex}");
            try
            {
                await ShowErrorAsync(xamlRoot, $"启动登录对话框失败：{ex.Message}");
            }
            catch { /* 忽略 */ }
        }
    }

    /// <summary>
    /// 外置登录（Yggdrasil）：输入邮箱/密码登录指定服务器（内置 LittleSkin 或用户自定义源）。
    /// </summary>
    public async Task AddExternalAccountAsync(XamlRoot xamlRoot, YggdrasilServer server)
    {
        var title = $"外置登录 · {server.Name}";
        await ShowExternalLoginDialogAsync(xamlRoot, server, title, "登录");
    }

    /// <summary>
    /// 为已保存的外置账户重新登录（令牌失效后使用，服务器地址沿用账户中记录的值）。
    /// </summary>
    public async Task ReloginExternalAsync(XamlRoot xamlRoot)
    {
        var account = SelectedAccount;
        if (account is null || account.Type != AccountType.Yggdrasil) return;

        var server = new YggdrasilServer
        {
            ApiRoot = account.AuthServer,
            Name = string.IsNullOrWhiteSpace(account.AuthServerName) ? account.AuthServer : account.AuthServerName,
        };
        var title = $"重新登录 · {server.Name}（{account.PlayerName}）";
        await ShowExternalLoginDialogAsync(xamlRoot, server, title, "登录");
    }

    private async Task ShowExternalLoginDialogAsync(XamlRoot xamlRoot, YggdrasilServer server,
        string title, string primaryText)
    {
        var emailBox = new TextBox { PlaceholderText = "邮箱 / 用户名" };
        var passwordBox = new PasswordBox { PlaceholderText = "密码" };
        var hint = new TextBlock
        {
            Text = $"登录 {server.Name}（{server.ApiRoot}）",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 340,
                Children = { hint, emailBox, passwordBox },
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var auth = await YggdrasilService.Instance.AuthenticateAsync(server, emailBox.Text, passwordBox.Password);

            if (auth.Profiles.Count == 0)
            {
                await ShowErrorAsync(xamlRoot,
                    $"登录成功，但该账号在 {server.Name} 尚未创建角色。请先到网站创建一个 Minecraft 角色后再登录。");
                return;
            }

            // 多角色：让玩家自己选择要用的角色
            var profile = auth.Profiles[0];
            if (auth.Profiles.Count > 1)
            {
                var picker = new ComboBox
                {
                    MinWidth = 300,
                    IsEditable = false,
                    ItemsSource = auth.Profiles.Select(p => $"{p.Name}（{p.Id[..Math.Min(8, p.Id.Length)]}…）").ToList(),
                    SelectedIndex = 0,
                };
                var pickDialog = new ContentDialog
                {
                    Title = $"选择要使用的角色 · {server.Name}",
                    Content = new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "该账号下有多个角色，请选择本次登录要使用的角色。",
                                TextWrapping = TextWrapping.Wrap,
                                FontSize = 12,
                                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                            },
                            picker,
                        },
                    },
                    PrimaryButtonText = "使用该角色",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot,
                };
                if (await pickDialog.ShowAsync() != ContentDialogResult.Primary) return;
                var index = picker.SelectedIndex;
                if (index < 0 || index >= auth.Profiles.Count) return;
                profile = auth.Profiles[index];
            }

            var account = YggdrasilService.Instance.CreateAccount(server, auth, profile);
            await _authService.SaveExternalAccountAsync(account);
            Refresh();
            LogService.Instance.Log($"外置登录成功：{account.PlayerName}（{server.Name}）");
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(xamlRoot, ex.Message);
            LogService.Instance.Log($"外置登录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 添加离线账户：弹出输入框让用户输入玩家名。
    /// </summary>
    public async Task AddOfflineAccountAsync(XamlRoot xamlRoot)
    {
        var input = new TextBox
        {
            PlaceholderText = "玩家名：3-16 位，仅限英文字母、数字、下划线",
            MaxLength = 16,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var dialog = new ContentDialog
        {
            Title = "添加离线账户",
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            // 固定内容宽度：直接放 TextBox 时，输入内容后对话框会按文本重新测量而缩窄
            Content = new StackPanel
            {
                MinWidth = 340,
                Children = { input },
            },
            XamlRoot = xamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            _authService.CreateOfflineAccount(input.Text);
            Refresh();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(xamlRoot, ex.Message);
        }
    }

    /// <summary>切换当前账户为选中账户。</summary>
    public void SwitchSelected()
    {
        if (SelectedAccount is null) return;
        try
        {
            _authService.SetCurrent(SelectedAccount);
            Refresh();
        }
        catch (Exception ex)
        {
            LogService.Instance.Log($"切换账户失败：{ex.Message}");
        }
    }

    /// <summary>点击账户卡片：点选为待操作账户（再点"切换到此账户"生效），选中卡片描边高亮。</summary>
    public void SelectCard(Account account)
    {
        if (account is null) return;
        SelectedAccount = account;

        foreach (var a in Accounts)
        {
            a.IsSelected = ReferenceEquals(a, account);
        }
    }

    /// <summary>删除选中账户。</summary>
    public void DeleteSelected()
    {
        if (SelectedAccount is null) return;
        _authService.Delete(SelectedAccount);
        Refresh();
    }

    /// <summary>刷新列表与选中项。</summary>
    private void Refresh()
    {
        var current = _authService.CurrentAccount;

        Accounts.Clear();
        foreach (var account in _authService.Accounts)
        {
            account.IsCurrent = ReferenceEquals(account, current);
            account.IsSelected = ReferenceEquals(account, current); // 刷新后选中态回到当前账户
            Accounts.Add(account);
        }

        SelectedAccount = current;
        OnPropertyChanged(nameof(CurrentAccount));
        OnPropertyChanged(nameof(CanSwitch));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanRelogin));

        _ = LoadAvatarsAsync();
    }

    /// <summary>为列表中的账户异步加载真实头像（微软 / 外置取角色皮肤，离线按 UUID 取默认皮肤）。</summary>
    private async Task LoadAvatarsAsync()
    {
        foreach (var account in Accounts.ToList())
        {
            if (!string.IsNullOrEmpty(account.HeadImagePath)) continue;
            try
            {
                var path = await SkinService.Instance.GetHeadImagePathAsync(account);
                if (string.IsNullOrEmpty(path)) continue;
                _dispatcher.TryEnqueue(() => account.HeadImagePath = path);
            }
            catch (Exception ex)
            {
                LogService.Instance.Log($"加载头像失败（{account.PlayerName}）：{ex.Message}");
            }
        }
    }

    private static void TryHideDialog(ContentDialog dialog)
    {
        try
        {
            if (dialog.IsLoaded)
            {
                dialog.Hide();
            }
        }
        catch
        {
            // 对话框可能已被关闭
        }
    }

    private static async Task ShowErrorAsync(XamlRoot xamlRoot, string message)
    {
        var dialog = new ContentDialog
        {
            Title = "操作失败",
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
    }
}
