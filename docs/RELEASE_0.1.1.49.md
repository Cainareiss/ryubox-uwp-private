# Ryujinx Xbox UWP 0.1.1.49 - Deep Trace

## Correção principal

O contador de tempo do guest (`TickSource`) mantinha `Stopwatch`, frequência e
acumuladores em campos estáticos, compartilhados por todas as threads ARM. As
leituras atualizavam esse estado sem sincronização. A build T49 torna todo o
estado local à instância, serializa leitura/suspend/resume e usa no Xbox o mesmo
relógio monotônico baseado em `Environment.TickCount64` para guest, scheduler,
timers, áudio e SurfaceFlinger.

## Diagnóstico

Durante a inicialização até o primeiro frame, o watchdog cria logs separados e
correlacionados por `sample=N`. Consulte `DEEP_TRACE_0.1.1.49.md` para a relação
dos arquivos e como cruzá-los.

## Validações desta entrega

- CoreHost gerenciado: compilado sem erros.
- CoreHost NativeAOT x64: publicado e exportações nativas verificadas.
- XAML/UWP Release x64: compilado sem erros.
- Teste concorrente do relógio: 800.005 leituras, zero regressões, suspend/resume
  aprovado.
- MSIX: assinatura SHA-256 válida, versão 0.1.1.49 e alvo Windows.Xbox.
- Pacote D3D11-only: host nativo, CoreHost e SPIRV-Cross presentes; Vulkan,
  Mesa/Dozen e DXIL ausentes.

O teste local completo de Shovel Knight não foi executado porque o sandbox do PC
não contém firmware instalado. A tentativa foi encerrada pela validação esperada
antes de criar o Switch; não houve crash do host.
