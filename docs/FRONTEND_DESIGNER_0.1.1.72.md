# Ryubox frontend 0.1.1.72

Este pacote contém o frontend UWP editável da versão Controller Focus.

## Onde editar

- `MainPage.xaml`: composição e conteúdo da tela.
- `App.xaml`: cores, estilos, templates e estados visuais.
- `MainPage.xaml.cs`: biblioteca, controle, foco, capas e ações.

## Foco do controle

O template `CarouselListViewItemStyle`, em `App.xaml`, mantém dois sinais
independentes:

- seleção: aro ciano, brilho e selo `A INICIAR`;
- foco real: segundo aro rosa ativado pelo `FocusState` do item.

O código centraliza e transfere o foco para o container selecionado somente
quando o usuário já está navegando na biblioteca. Isso evita roubar o foco da
busca ou dos botões.

## Capas embutidas

O MSIX oficial contém 1.443 capas em `Assets/CoverArt`. Para manter este ZIP de
designer pequeno, as imagens não são duplicadas aqui. O índice está incluído e
as capas podem ser regeneradas com:

```powershell
.\Import-RyuboxCoverArt.ps1 -SourceDirectory 'E:\595310801_NintendoSwitch2.5D'
```

Capas locais ao lado do jogo ou dentro da pasta `covers` têm prioridade sobre
o catálogo embutido.
