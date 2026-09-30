# Depuração remota do Ryujinx UWP no Xbox

## Ambiente preparado

- Visual Studio Professional 2022 instalado.
- Windows SDK 10.0.26100 instalado.
- Workload Universal Windows Platform instalado.
- Depurador remoto UWP e `msvsmon.exe` disponíveis no Visual Studio.
- WinDbg instalado para capturar falhas nativas fora do código gerenciado.
- Extensões do VS Code instaladas: C# Dev Kit, C/C++ e PowerShell.

O VS Code fica configurado para edição, compilação, instalação local e leitura dos logs. A anexação ao processo UWP do Xbox deve ser feita pelo Visual Studio 2022, que fornece o transporte remoto e o debugger nativo necessários para `vulkan-1.dll`, Mesa Dozen e o CoreHost AOT.

## Conexão com o Xbox

1. Coloque o PC e o Xbox na mesma rede local. O alvo configurado é `192.168.100.20`.
2. O Device Portal foi validado em `https://192.168.100.20:11443`.
3. No Visual Studio 2022, abra `Ryujinx.sln`.
4. Selecione **Debug > Other Debug Targets > Debug Installed App Package**.
5. Em **Connection Type**, escolha **Remote Machine or Device**, informe `192.168.100.20` e use autenticação Universal/PIN.
6. Informe o PIN exibido pelo Xbox enquanto o pareamento estiver ativo. O PIN não é salvo neste projeto.
7. Selecione o pacote Ryujinx UWP e escolha a opção equivalente a **não iniciar automaticamente; anexar quando o app iniciar**.
8. Inicie o Ryujinx pelo Xbox e depois inicie o jogo.

## Breakpoints recomendados

Coloque breakpoints nestes pontos:

- `src/Ryujinx.Uwp.CoreHost/CoreHostExports.cs`: `Start`;
- `ConfigureVulkanDriver`;
- `VulkanRenderer.Create`;
- `renderer.Initialize`.

Também habilite a parada em exceções nativas/Win32. O objetivo é capturar o módulo que devolve `VK_ERROR_OUT_OF_HOST_MEMORY`, confirmar o caminho real do `vulkan-1.dll` e verificar se o Xbox está usando a versão correta do pacote.

## Atalhos do VS Code

No menu **Terminal > Run Task**, estão disponíveis:

- `Ryujinx Xbox: compilar MSIX assinado`;
- `Ryujinx Xbox: instalar MSIX local`;
- `Ryujinx Xbox: abrir app local`;
- `Ryujinx Xbox: abrir logs locais`;
- `Ryujinx Xbox: abrir Device Portal`.

O projeto deve ser atualizado no Xbox para a versão mais recente antes de coletar novos dumps. Os logs anteriores apontavam para o pacote `0.1.0.2`.
