# Ryubox Astra 6 — Parte 1: Fundação

Data de fechamento técnico: 2026-09-08
Pacote: `0.2.0.114` (`Release`, `x64`)

## Resultado

A fundação solicitada na Parte 1 foi implementada no CoreHost, frontend UWP, host D3D11 nativo, ARMeilleure/PTC e caches de GPU. O pacote foi recompilado e assinado após a reconexão do HD externo.

Este fechamento confirma implementação, compilação e integridade do pacote. A matriz de regressão em hardware Xbox continua sendo o gate de execução antes de iniciar a Parte 2; ela não foi marcada como aprovada sem teste real.

## Implementado

### Sessão e apresentação

- `SessionId` monotônico compartilhado entre CoreHost, frontend, janela D3D11 e presenter nativo.
- Estados explícitos da sessão e proprietário da apresentação (`Frontend`/`Game`).
- Callbacks atrasados de sessões antigas são descartados.
- Contadores independentes para frames produzidos pelo guest, aceitos, enviados ao swapchain, realmente apresentados e frames do frontend.
- Início e encerramento zeram métricas e ações pendentes sem reaproveitar estado da sessão anterior.

### Perda do dispositivo D3D11 e encerramento

- Detecção imediata e registro de `GetDeviceRemovedReason`.
- Sessão passa a `Faulted`, cancela tarefas e interrompe a GPU antes da desmontagem.
- Threads e scheduler são encerrados antes do descarte de `Switch`, renderer, GPU, caches e recursos temporários.
- Dispositivo perdido não é reutilizado; o frontend tenta reconstruir o dispositivo e religar o painel.
- Working set é registrado antes e depois do cleanup.

### Memória, PTC e JIT

- Orçamento global por pressão com patamares padrão de 3,30 / 3,50 / 3,70 / 3,85 GiB, limitado ao teto detectado do host.
- Telemetria de working set, private commit, heap gerenciado, JIT, PTC, compilador, texturas, render targets, staging/readback, áudio e alocações nativas.
- Pretradução PTC preservada e adaptativa: lotes 512 / 256 / 128; adiada no nível crítico.
- JIT com telemetria de regiões, uso, fragmentação, taxa de alocação e falhas.
- Reaproveitamento seguro de código frio antes do teto, usando retirada por épocas e restauração do dispatch para impedir ponteiros obsoletos.
- Arenas do compilador e alocações nativas passam a alimentar o orçamento global.

### Shaders e caches D3D11

- Compilação HLSL passa por fila controlada sob pressão.
- Fonte e temporários são contabilizados e liberados cedo.
- Cache em memória é reduzido sob pressão alta, preservando o cache de shaders em disco.
- Cache de texturas aplica redução progressiva de alvo e despejo LRU conforme a pressão.

### Loader

- Detecção do contêiner por assinatura (`HEAD`, `PFS0`, `NRO0`) com extensão apenas como fallback.
- XCI é encaminhado a `LoadXci`, NSP a `LoadNsp`, NCA a `LoadNca` e NRO a `LoadProgram`.
- XCI usa stream compatível com arquivo autorizado pelo broker UWP.

## Validações concluídas

- CoreHost .NET/NativeAOT: compilação `Release x64` concluída sem erros.
- Host nativo C++ D3D11: compilação concluída sem erros.
- Aplicativo UWP: rebuild e geração do MSIX concluídos sem erros.
- Manifesto interno: versão `0.2.0.114`.
- Assinatura: válida, `CN=Ryujinx Xbox Dev`.
- SHA-256 do MSIX: `52C4AC82FC1D6B6CA9E55C059CE7B0EF8DBD237E87097BE4CF0A67757AC3B6E7`.
- A DLL nativa contida no MSIX corresponde ao binário recém-compilado.
- A DLL CoreHost contida no MSIX corresponde ao binário final copiado para o aplicativo.

## Gate pendente no Xbox

Antes da Parte 2, instalar este pacote no Xbox Series S e executar a matriz indicada no plano, especialmente:

- estabilidade de início/encerramento e início de uma segunda sessão;
- ausência de reaproveitamento de callbacks e dispositivos de sessões antigas;
- PTC ativo sem atingir limite rígido do JIT;
- comportamento dos patamares de memória sob jogos pesados;
- abertura correta de XCI;
- regressões visuais e de apresentação nos títulos-base do plano.

O primeiro NativeAOT desta rodada falhou com `0xC0000006` enquanto o HD externo estava desconectado; o log do Windows registrou erro de paginação no disco. Após reconectar o HD, limpar e recompilar, todos os builds acima foram concluídos. Para validar no console, mantenha o disco e a conexão USB estáveis durante todo o teste.

## Artefato

`E:\ports\ryujinx uwp\src\Ryujinx.Uwp.App\AppPackages\Ryujinx.Uwp.App_0.2.0.114_x64_Test\Ryujinx.Uwp.App_0.2.0.114_x64.msix`

## Hotfix de apresentação e input — 0.2.0.115

Os logs de `C:\Users\Cainã\Downloads\Logs (1).zip` mostraram que o CoreHost não estava bloqueado em 37%: Captain Toad e Alien Hominid concluíram o start, produziram centenas de frames e continuaram executando. A telemetria do compositor, porém, registrava cópias D3D11 bem-sucedidas com `present=0/0`, `guestFramesSubmittedToSwapChain=0` e `guestFramesVisibleOnAppSurface=0`. O percentual congelado era o último frame do frontend antes da troca de proprietário da apresentação.

Correções aplicadas:

- o presenter direto agora executa `PresentBackBuffer` depois de `CopyTexture`;
- o presenter de compatibilidade também apresenta o backbuffer depois do upload ou reuso do frame;
- falha de `Present` não cai em uma segunda cópia/apresentação do mesmo frame;
- removido o `Button` XAML invisível usado como receptor de foco, que era ativado pelo botão A do Xbox e causava clarão/clique adicional;
- removida a segunda rota de teclado da própria página, mantendo uma única origem via `CoreWindow`;
- adicionada barreira de liberação para impedir que o A/Enter usado no launch seja enviado aos primeiros frames do jogo.

Validação:

- CoreHost NativeAOT, host nativo C++ e aplicativo UWP: 0 erros;
- smoke test nativo: blit legado/V2, crop, orientação, `Present` repetido, isolamento do frontend, resize e duas sessões aprovados;
- manifesto interno: `0.2.0.115`, x64, Windows Universal/Xbox;
- assinatura verificada sem avisos ou erros;
- CoreHost empacotado verificado contra o binário recém-compilado: `EA0A5BF704994E46E19D77A36D645E05CA56BCFE2F01E28E86744B0586DD1887`;
- SHA-256 do MSIX: `9C75B3E506B951A9163D56436DE14A734CEB2ECA2FDFD5EFFD8A0D2B5077341A`.

Artefato corrigido:

`E:\ports\ryujinx uwp\artifacts\xbox\Release\AppPackages\Ryujinx.Uwp.App_0.2.0.115_x64_Test\Ryujinx.Uwp.App_0.2.0.115_x64.msix`

## Reavaliacao do lote Logs (3).zip — candidata 0.2.0.116

### Evidencias e limites

O lote de 09/09/2026 registra 12 inicios com CoreHostStartSuccess e 11 eventos FirstVisibleGuestPresent. O botao A foi confirmado como corrigido pelo usuario. A orientacao melhorou em varios titulos. Essas melhorias da 0.2.0.115 foram preservadas.

As falhas fatais explicitas restantes sao Mario Wonder (03:40:57 UTC) e Blasphemous 2 (04:02:07 UTC): esgotamento do JIT em duas regioes/512 MiB. Blasphemous estava com pressao Normal, portanto o teto por si so ainda encerrava a sessao. A recuperacao por epocas podia permanecer bloqueada por threads suspensas dentro de chamadas guest com enderecos nativos de retorno vivos.

Pikachu produziu milhares de frames, com ProcessFrame chegando a aproximadamente 30 segundos, e nao registra fatal nesse lote. Isso NAO comprova que seu bloqueio na selecao de personagem tenha a mesma causa do OOM. Brilliant Diamond nao registra primeiro frame visivel. Esses casos continuam exigindo reproducao no console; nao sao declarados resolvidos apenas pela compilacao.

### Alteracoes implementadas

- Despacho UWP com protecao por funcao em execucao: chamadas entre traducoes retornam ao dispatcher com PC/registradores guest preservados. Nao ha enderecos nativos de retorno entre traducoes nesse modo. Uma thread suspensa conserva sua traducao, sem impedir a liberacao das demais.
- Lookup, publicacao, protecao e invalidacao usam o mesmo bloqueio; uma traducao invalidada em uso so e liberada apos seu ultimo leitor retornar.
- Reclaim antecipado preservado; ao esgotar o espaco, tenta recuperar funcoes nao protegidas e mapear novamente os mesmos bytes compilados. Nao aumenta o teto de 512 MiB nem repete todo o compilador.
- Falha de lookup no dispatcher nativo retorna pela fronteira nativa antes de propagar a excecao em codigo gerenciado. Falha guest e encaminhada ao CoreHost da sessao correspondente, que sinaliza Faulted, cancela a GPU e agenda cleanup.
- Removido bloqueio Sync da rota de erro da GPU: StopCore ja possui esse bloqueio ao aguardar a thread, gerando uma espera circular.
- Timeout de join da GPU preserva os recursos e bloqueia um novo Start. Nao descarta o renderer enquanto a thread ainda esta viva.
- Encerramento dos tradutores em background aguarda o fim real antes de soltar suas referencias. Cleanup do Translator tambem cobre falhas na inicializacao do PTC.
- Cancelamento do watchdog nao descarta seu WaitHandle enquanto a thread ainda pode usa-lo.
- Telemetria de memoria/JIT tambem e emitida no loop GPU a cada segundo quando esse loop progride. Coleta pesada por compilacao sob pressao critica foi limitada a uma tentativa por dois segundos.
- PTC permanece ativo; formato 7023 e seletor pinned-v1 impedem reutilizar codigo antigo com chamadas nativas aninhadas. O primeiro boot desse formato pode reconstruir o cache; nao foram apagados caches em disco, keys, firmware ou saves.
- Limpeza do clique de mouse pendente ao entrar no jogo evita prender a barreira de liberacao de input. A rota do botao A validada nao foi reintroduzida.

### Testes locais

- 31 verificacoes JIT/memoria aprovadas: alocacao/reuso, falha de commit, epocas, corridas de dispose, protecao por funcao, ARM64 BL/RET/BR executado em x64 nos dispatchers gerenciado e nativo, invalidacao dentro de SVC e propagacao de falha de traducao.
- 600 MiB cumulativos de mapeamentos executaveis reutilizaram uma unica regiao com uma funcao protegida, sem falha de alocacao. Isso e teste sintetico, nao sessao longa de Mario Wonder.
- 15 verificacoes PTC aprovadas, incluindo persistencia, limites de warm-up e corridas de encerramento.
- Smoke nativo D3D11 aprovado: inicializacao, blits legado/V2, crop, flip, presents repetidos, isolamento do frontend, resize e duas sessoes.
- Evidencias em artifacts/jit-astra6-116.log, artifacts/ptc-astra6-116.log e artifacts/presentation-astra6-116-resume.log.

### Gate da Parte 1

Ainda nao aprovado para avancar automaticamente a Parte 2. Repetir no Xbox as sessoes longas de Wonder/Blasphemous, a selecao de personagem de Pikachu, Brilliant Diamond e o ciclo iniciar/sair/reiniciar. Os testes locais nao substituem essas verificacoes. O build interrompido da madrugada nao chegou a produzir o MSIX 0.2.0.116; sua retomada esta registrada em artifacts/build-astra6-116-resume.log.

### Empacotamento concluido em 09/09/2026

- CoreHost NativeAOT publicado e aplicativo UWP compilado com zero erros (avisos preexistentes permanecem).
- MSIX 0.2.0.116 x64 gerado; tamanho: 112835634 bytes.
- SignTool verify /pa: assinatura verificada com sucesso.
- CoreHost dentro do MSIX identico ao recem-compilado: SHA-256 `84FC5EF1A537C4380DBF82639B744D70E434F6D0837A85A5B9652F4E6518E0DE`.
- SHA-256 do MSIX: `82FB61E172D2AF32A6CAB63B944BB8E0E5F456CAD3B2BC09F8C9C26A999BE530`.
- Artefato: `E:\ports\ryujinx uwp\artifacts\xbox\Release\AppPackages\Ryujinx.Uwp.App_0.2.0.116_x64_Test\Ryujinx.Uwp.App_0.2.0.116_x64.msix`.
- Certificado publico e dependencias x64 acompanham a pasta do pacote. Nao instalado automaticamente nesta rodada.
- Usuario confirmou novamente desligar o PC apos concluir. A aprovacao funcional no Xbox continua pendente conforme o gate acima.
