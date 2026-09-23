# Frontend Ryujinx Xbox UWP

O layout principal está em `MainPage.xaml` e a lógica da tela está em
`MainPage.xaml.cs`.

Arquivos principais:

- `MainPage.xaml`: biblioteca, painel de sessão, botões e superfície gráfica.
- `MainPage.xaml.cs`: seleção de jogos, eventos de mouse/controle e início da sessão.
- `UwpInputHost.cs`: leitura do controle Xbox.
- `UwpGraphicsHost.cs`: diagnóstico e host gráfico UWP.
- `UwpCoreSession.cs`: ligação entre a tela e o CoreHost.

O botão `Iniciar jogo` fica no painel de sessão. Cada item da biblioteca também
possui um botão `Iniciar`; duplo clique no item e o botão A do controle executam
o mesmo fluxo.

Para recompilar depois de alterar o design, abra o projeto no Visual Studio ou
execute `eng\\Build-RyujinxXbox.ps1` na raiz do projeto.

## Design system (App.xaml)

O visual foi redesenhado em torno de um conceito "console dev-kit": fundo quase
preto azulado, acento verde-Ryujinx para estados prontos, âmbar para pendências
e vermelho só para erro. Dados técnicos (nomes de arquivo, status, atalhos)
usam fonte monoespaçada (Cascadia Mono/Consolas) para lembrar um readout de
firmware; rótulos de UI usam Segoe UI. Isso é a assinatura visual do app — não
adicione gradientes ou cores fora da paleta definida em `App.xaml`.

Todos os tokens (cores, brushes, estilos de texto e botão) ficam em
`App.xaml > Application.Resources`. Para mexer no visual:

- Cores: `Color*` / `*Brush` no topo do arquivo.
- Tipografia: `EyebrowStyle`, `PageTitleStyle`, `SectionHeaderStyle`,
  `BodyTextStyle`, `MonoValueStyle`, `HintBarStyle`, `FooterTextStyle`.
- Botões: `PrimaryButtonStyle` (ação principal), `SecondaryButtonStyle`
  (ações auxiliares), `LaunchButtonStyle`/`RowLaunchButtonStyle` (variações).
  Os templates têm estados de foco explícitos (`FocusRing`) pensados para
  navegação por controle/D-pad a distância — não remova o `FocusStates`.
- Lista de jogos: `GameListViewItemStyle` desenha a barra de seleção lateral
  e o destaque de foco por item.

`MainPage.xaml.cs` ganhou um helper `SetStatus(TextBlock, texto, StatusTone)`
que colore os textos de status (Chaves/Firmware/Gráfico/Controle) conforme
Ok/Warning/Error, buscando os brushes do dicionário de recursos do app. Toda a
lógica original (seleção de pasta, scan, sessão, gamepad) permanece intacta —
só a apresentação mudou.

## Estado de launch e firmware

O botao `Iniciar jogo` fica no painel de sessao. Um toque no item apenas o
seleciona; duplo clique e o botao A do controle iniciam o jogo somente quando
a aba `Home` esta ativa. As abas `Prod Keys`, `Config` e `Sobre` nao disparam o
jogo por engano.

O seletor de firmware fica na mesma aba `Prod Keys` das chaves. O arquivo ou a
pasta escolhida e persistido no sandbox UWP, copiado para
`LocalState\\firmware-cache` antes do launch e mostrado tambem no resumo da
aba `Home`.

O inicio do CoreHost possui timeout de 15 segundos. Se o backend grafico
travar, o overlay e encerrado, a selecao e liberada e o detalhe e gravado em
`errors.log`/`corehost-error.log`, sem deixar o menu preso tentando reabrir o
jogo depois que o aplicativo for reiniciado.

## Overlay de carregamento

O overlay usa uma barra deterministica de 0% a 100% com texto percentual
centralizado; nao ha mais spinner como indicador principal. A copia de uma
pasta Contents atualiza o percentual por arquivo e um firmware ja validado
mostra `FIRMWARE PRONTO` sem exibir novamente os botoes de instalacao.

Os scrollbars visuais ficam ocultos para o uso no Xbox. A aba `Prod Keys`
mantem o bloco de firmware no topo, fora de qualquer area rolavel. O D-pad
move o foco entre os controles fora da Home e A ativa o controle focado.

O `BusyOverlay` fica no final do `Grid` raiz de `MainPage.xaml`, portanto cobre
a interface sem alterar o layout das abas. O code-behind controla a mensagem
por `ShowBusyOverlay`, atualiza a etapa por `UpdateBusyOverlay` e sempre fecha
com `HideBusyOverlay` em blocos `finally`.

O overlay é usado na atualização da biblioteca, importação de keys, cópia do
firmware para `LocalState\\firmware-cache`, preparação do jogo e limpeza do
shader cache. Para trocar o visual, edite apenas o bloco `BusyOverlay` e os
estilos/cores em `App.xaml`; mantenha os nomes dos elementos para preservar a
lógica.

## Capas locais e correção de recursos UWP

A biblioteca usa `GridView` com cartões e procura capas `.jpg`, `.jpeg`, `.png`
ou `.webp` ao lado do jogo e na pasta `covers` da pasta principal. Os nomes
`CoverImage`, `SelectedGameCoverImage`, `SelectedGameCoverPlaceholder` e
`SelectedGameCoverExtensionText` são usados pelo code-behind e devem ser
preservados ao redesenhar os cartões.

Os recursos de cor/fontes acessados em C# devem ser obtidos por
`Application.Current.Resources`. O dicionário `Resources` da página pode estar
vazio no Xbox mesmo quando `StaticResource` funciona no XAML; usar o dicionário
local causa o crash `0x80004005` durante `MainPage_Loaded`.
