# Handoff para Codex Cloud — Ryubox UWP/D3D12

Atualizado em 22/09/2026. Este documento descreve o estado comprovado do
backend D3D12, o bloqueio atual e uma ordem segura de continuação. Não assumir
que ausência de crash significa renderização correta.

## Repositório e branch

- Repositório privado: `Cainareiss/ryubox-uwp-private`
- Branch padrão: `main`
- Snapshot inicial validado: `40d34bd21edc915132dd5c21c99b91f923a64d97`
- Clonar com submódulos: `git clone --recurse-submodules <url>`

Não adicionar ROMs/NSPs, firmware, `prod.keys`, `title.keys`, logs pessoais,
MSIX gerados, capturas, caches ou mídia local ao Git.

## Estado comprovado

O caminho básico D3D12 funciona e jogos 2D já chegaram a 60 FPS. Jogos 3D
ainda exercitam contratos incompletos de textura/render pass e têm custo alto
de CPU/JIT. O descriptor ring não mostrou falta de capacidade nos Logs 20;
seu problema observado é churn de descritores.

Correções já presentes incluem:

- backend GAL D3D12 e CoreHost NativeAOT;
- descriptor ring instrumentado;
- cache/JIT com orçamento, preflight, retirement e proteção contra dispatch
  nulo;
- accounting separado de memória guest/JIT/managed/GPU;
- liberação do backing físico guest quando o contador de referências chega a
  zero;
- proteção contra combinação RTV/DSV de dimensões incompatíveis;
- telemetria de FIFO, apresentação, memória, descritores e watchdog.

No teste mais recente de MARVEL Cosmic Invasion, o decommit de páginas físicas
reduziu o commit privado de aproximadamente 4,92 GiB para 4,12 GiB e o guest
de aproximadamente 2,77 GiB para 2,32 GiB. O processo avançou além do bloqueio
anterior de memória, mas não produziu imagem real.

## Bloqueio imediato: compute shader X3694

O primeiro erro acionável atual de MARVEL é:

```text
D3D12 program HRESULT=0x80070057
error X3694: race condition writing to shared resource detected,
consider making this write conditional.
error location reached from this location
```

Stack principal:

```text
D3D12NativeDevice.CreateProgram
D3D12SpirvCompiler.CreateProgram
D3D12Renderer.CreateProgram
ShaderCache.GetComputeShader
ComputeClass.SendSignalingPcasB
GPFifoProcessor.Process
```

Isso não é simplesmente falha de apresentação/tela preta. O guest chega ao
compute, mas o HLSL gerado é rejeitado pelo FXC por acesso `groupshared` em
controle divergente. O D3D12 recebe zero draws/presents úteis depois disso.

### Como corrigir o X3694

1. Em `D3D12NativeDevice.CreateProgram`, preservar em arquivo diagnóstico o
   HLSL exato, estágio, SPIR-V original e mensagem do compilador quando a
   compilação falhar. Usar hash no nome e nunca registrar dados do jogo.
2. Criar um teste mínimo com o shader capturado, reproduzindo X3694 sem iniciar
   o jogo.
3. Auditar a tradução de memória compartilhada/barriers em:
   `Ryujinx.Graphics.Shader`, `D3D12SpirvCompiler` e o caminho SPIRV-Cross.
4. Não remover barriers e não mascarar X3694 com flags de compilação. Uniformizar
   apenas a região de controle necessária ou predicar a escrita compartilhada,
   preservando a sincronização de todo o grupo.
5. Validar resultado do compute em buffer, não apenas sucesso de compilação.
6. Manter o fallback HLSL/DXBC explícito até que a ABI SPIR-V→DXIL esteja
   documentada e validada; não chamar exports inferidos de DLL de terceiros.

Aceite imediato: shader mínimo compila, produz o buffer esperado e MARVEL
ultrapassa esse ponto sem X3694. Depois disso, exigir primeiro frame não preto.

## Prioridades seguintes

### 1. Render pass, depth e texturas 3D

Manter por subresource:

- resource format real/tipeless e formato de SRV/RTV/DSV/UAV;
- mip, layer, slice 3D, plane, sample count e quality;
- estado e transições por subresource, incluindo UAV barrier;
- viewport/scissor e orientação efetivos;
- compatibilidade de todos os MRTs e DSV antes de `OMSetRenderTargets`.

O fallback que desanexa um DSV menor evita crash, mas perde depth. Implementar
um depth proxy compatível por chave `(resource, mip, layer, width, height,
format, samples)`, com cópia/resolve quando semanticamente permitido. Não fazer
stretch silencioso de depth. Horizon Chase é o teste: chão, carros, oclusão,
sombras e materiais precisam permanecer corretos durante uma corrida.

### 2. Primeiro frame e tela preta

Registrar uma linha temporal correlacionada por sessão:

- tradução/compilação e cache hit/miss de shader;
- criação/cache de PSO;
- cópias e binds de descriptor tables;
- gravação e fechamento de command list;
- submit, fence wait e present;
- QueueBuffer/CancelBuffer do SurfaceFlinger.

Não promover todo `CancelBuffer` para frame: isso pode apresentar conteúdo
inválido. O workaround existente deve continuar limitado ao caso inicial já
documentado. Mario U e Captain Toad precisam produzir primeiro frame real e
manter ao menos 60 segundos de execução.

### 3. Churn de descritores

Cachear tabelas pela identidade completa de programa/layout e recursos:

```text
(program, layout, CBV/SRV/UAV, sampler, view, range, offset)
```

Reutilizar tabela quando nada mudou, copiar somente slots sujos e agrupar
cópias por tabela. Medir `copies/draw`, `heapSwitches/draw`, waits e pressão.
Não aumentar heaps sem evidência de espera por fence ou exaustão.

### 4. CPU/FIFO e paralelismo

Primeiro medir tempos de tradução, PSO, descriptor copy, command recording,
submit, wait e present. Depois mover somente tradução/compilação de shaders e
preparação imutável de descritores para workers. Command list e submit devem
continuar com proprietário único e recursos em voo protegidos por fences. Não
ligar `gpuThreading` global como tentativa cega.

### 5. Memória/JIT

Preservar os guards atuais. Não elevar cegamente o limite de 5 GiB nem reduzir
o headroom: testes anteriores reproduziram erro Windows 1455 perto de 4,97 GiB.

Validar:

- páginas guest só são decommitadas após referência física chegar a zero;
- página reaproveitada volta zerada quando o contrato exige;
- mapeamentos compartilhados não são decommitados prematuramente;
- eviction não permite duplicação infinita de código ainda protegido por epoch;
- várias sessões sequenciais retornam ao baseline de memória.

## Regressões obrigatórias

Executar primeiro testes unitários/regressões e depois WARP/GPU física com
debug layer. Cobrir RTV/DSV, mip/layer/3D, MRT, MSAA/resolve, UAV barriers,
arrays e formatos. Em seguida:

1. Alien Hominid, Sonic Mania e Dead Cells: preservar orientação, áudio e 60 FPS.
2. Horizon Chase: corrida 3D sem geometria atravessando o chão.
3. Mario U e Captain Toad: primeiro frame e 60 s sem tela preta.
4. Cuphead: frametime sem quedas por recompilação/descriptor churn.
5. Z-A, Rivals e Terraria: sem OOM, `JitCacheFullException` ou erro 1455.
6. MARVEL: sem X3694 e com captura real do jogo.

## Comandos úteis

```powershell
dotnet build src/Ryujinx.HLE/Ryujinx.HLE.csproj -c Release --no-restore
dotnet publish Ryujinx.Uwp.CoreHost/Ryujinx.Uwp.CoreHost.csproj -c Release --no-restore
dotnet run --project eng/JitCacheRegression/JitCacheRegression.csproj -c Release --no-restore
```

O runner local usado neste ambiente está em
`eng/Run-CoreHostD3D12Direct.ps1`; ao portar para CI, remova caminhos absolutos
e forneça jogos/keys apenas como segredos/artefatos privados fora do repositório.

## Regra de reporte

Para cada correção, registrar: reprodução anterior, causa raiz, arquivos
alterados, teste de resultado, adapter, debug-layer output, pico de memória,
tempo até primeiro frame e regressões. Não anunciar “corrigido” com base apenas
em build verde, ausência de exception ou splash screen.
