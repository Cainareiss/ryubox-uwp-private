# Ryubox: auditoria de desempenho e plano de intervenção

## Conclusão executiva

O desempenho não está limitado por um único problema. Há custos concretos no caminho D3D11 de constantes, readbacks, queries e interop; existem também limitações deliberadas do modo seguro de CPU e políticas de memória que podem transformar pressão em pausas. A ordem correta é corrigir violações de contrato e desperdícios demonstráveis, medir a versão resultante e só então modificar decisões de segurança ou qualidade de compilação.

O alvo desta análise é Xbox Series S em UWP. O estado do código é posterior à 0.2.0.117, incluindo a rodada CPU/JIT já compilada localmente, mas ainda não publicada em um novo NativeAOT/MSIX. Os logs recebidos não identificam inequivocamente essa versão. Portanto, números do lote anterior não são um benchmark do código atual, e testes locais não certificam desempenho no console.[^1]

As prioridades imediatas são: corrigir o contrato das queries; tornar consultas não obrigatórias realmente não bloqueantes; eliminar readback desnecessário e alocações repetidas de constantes; coordenar coletas de memória; reduzir custo do dispatcher sem retirar pins. A migração para High-CQ, uma mudança global de backend ou um limitador de FPS não substituem essas ações.

## Evidência disponível e limites

Há três classes de evidência neste relatório:

- **Código confirmado:** o caminho e suas operações existem na implementação atual. Isso não demonstra sua frequência em cada jogo.
- **Medição do lote:** comportamento registrado em `Logs (6)`, anterior à validação das alterações mais recentes.
- **Hipótese de impacto:** mecanismo plausível que precisa de tempo, frequência e cenário controlado para ser quantificado.

O lote reúne 18 inicializações de 14 títulos. Sonic Mania apresentou mediana de 13 FPS em uma sessão com zero novas compilações HLSL; FireRed, 5 FPS nas mesmas condições de cache aquecido. Wonder chegou a 657.014 evictions de código JIT. Terraria, Super Mario Bros. 35 e New Super Mario Bros. U Deluxe tiveram sessões sem callback de apresentação. Em contraste, Mario 3D World alcançou mediana de 60 callbacks/s após uma inicialização de aproximadamente 111 segundos.[^1]

Essas medianas usam amostras periódicas de callbacks depois do primeiro frame e incluem zeros posteriores. Não demonstram que uma fase foi concluída, que todo frame contém imagem diferente ou que a orientação está correta. Também não há uma comparação controlada, da mesma cena, entre a versão antes estável a 60 FPS e a versão do lote.

O ZIP contém os 283 shaders que os logs registraram como persistidos; todos passaram na verificação estrutural DXBC. Isso afasta a explicação simples de cache ausente, mas não prova correção visual de shaders. Cache D3D11 e PTC são caches distintos, de GPU e de CPU respectivamente.[^1]

## Matriz de prioridade

P0 significa corrigir um risco de resultado incorreto ou falha antes de comparar FPS. P1 identifica desperdício concreto com potencial relevante. P2 exige perfil quantitativo ou tem escopo mais localizado.

| Prioridade | Área | Evidência | Intervenção necessária |
| --- | --- | --- | --- |
| P0 | Resultado de query de oclusão | Código entrega 16 bytes também para resultado UINT64 | Tamanho por tipo, erros explícitos e testes reais de queries |
| P1 | Polling de queries | Loop bloqueante, array e pin por tentativa | Poll não bloqueante, espera somente quando exigida, buffer de resultado sem heap |
| P1 | Vida útil de counters | Lista cresce até o encerramento | Separar pendentes de objetos ainda referenciados pelo guest |
| P1 | Constantes | Novo array/upload de 64 KiB a cada atualização | Pool/ring, dirty ranges e coalescência sem quebrar o cbuffer traduzido |
| P1 | Readback de buffers | CopyResource inteiro para uma leitura de quatro bytes | Mirror quando válido, staging por intervalo e readback diferido quando possível |
| P1 | Pressão de memória | Caminhos encadeados solicitam GC completo | Um controlador de trim, cooldown, metas de bytes e medição de pausas |
| P1 | CPU/dispatcher | Pins seguros ainda usam locks, busca e telemetria por função | Reduzir custos quentes mantendo invalidação e lifetime corretos |
| P1 | Jogos sem frames | Filas vazias ou trabalho sem buffer de apresentação | Investigar progresso guest e esperas, não aplicar limiter |
| P1/P2 | Shaders frios | Criação de programa pode compilar sincronicamente | Pré-aquecimento limitado e separar tradução/compilação da criação no contexto |
| P2 | Hazards e estado | Rebinds, arrays e invalidação conservadora | Dirty tracking e granularidade de escrita, com regressão visual |
| P2 | Interop D3D11 | Conversão de ponteiro para delegate em chamadas frequentes | Medir e testar cache por vtable ou function pointers compatíveis com AOT |
| P2 | Texturas e formatos | ASTC/ETC2 e conversões com etapas CPU | Cache por conteúdo/geração, orçamentos e caminhos GPU quando comprovados |
| P2 | Apresentação | Present(1) fixo; opção VSync não chega a esse argumento | Propagar política e medir latência de apresentação, sem mexer no crop |
| P2 | Logs e áudio | I/O síncrono; varredura de PCM por buffer | Escrita em lotes e amostragem, mantendo falhas e callbacks confiáveis |

## 1. Queries: correção e desempenho são inseparáveis

### Contrato do resultado

`D3D11CounterEvent` seleciona `Occlusion` para `SamplesPassed`, mas `Flush()` usa sempre um span de 16 bytes. O wrapper passa exatamente esse comprimento a `ID3D11DeviceContext::GetData`. O resultado de oclusão é UINT64, portanto oito bytes; estatísticas de stream output usam outra estrutura. Essa incompatibilidade está no código, mas não foi reproduzida nesta rodada nem associada a um crash específico dos jogos.[^2][^3]

A documentação oficial distingue os tipos de resultado e o estado ainda não pronto retornado por `GetData`. A correção deve respeitar o tipo da query, não truncar qualquer retorno ou transformar falhas em zero. [Microsoft: tipos de query](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_query), [Microsoft: GetData](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-getdata).

Também é necessário auditar `ResetCounter`, hoje vazio, contra a semântica esperada por `SemaphoreUpdater`. Um método vazio não prova que um jogo específico falhou, mas impede considerar completa a implementação dos counters.[^4]

### Polling e duração das esperas

`UpdateCounters()` percorre todos os counters e chama `Flush()`. O nome esconde que o método pode aguardar até 30 segundos por resultado, usando repetidamente `GetData` e `Thread.Yield`. Existe uma chamada no caminho de `ThreedClass` descrita como polling; portanto, a consulta de disponibilidade pode virar uma espera síncrona da thread que processa comandos.[^2][^4][^5]

Além disso, cada tentativa no wrapper de `GetData` cria um `byte[]`, aloca um `GCHandle` e copia o resultado. Isso ocorre mesmo quando o chamador já ofereceu memória de stack. Deve-se passar o span diretamente durante a chamada síncrona, sem manter ponteiros além dela.[^3]

O redesenho deve distinguir `TryGetResult`, que retorna imediatamente, de uma espera exigida pelo guest. A espera obrigatória precisa de cancelamento, diagnóstico de device loss e uma política de polling medida. Usar `DONOTFLUSH` indiscriminadamente pode impedir progresso se nunca houver submissão; retirar toda espera pode entregar resultados incorretos. Não substituir semáforos por sucesso artificial.

### Crescimento da lista

`ReportCounter()` adiciona objetos em `_counterEvents`; a lista é limpa na destruição do renderer. Counters já reportados retornam rapidamente de `Flush`, mas continuam sendo percorridos e referenciados. Em sessões com muitas queries, o custo da varredura pode crescer com o histórico, não só com o trabalho pendente.[^4]

É preciso uma coleção de queries pendentes que remova resultados concluídos, mantendo ownership separado para counters que ainda estejam referenciados pelo guest. O critério é custo proporcional às queries em voo, não ao número acumulado da sessão.

## 2. Constant buffers: desperdício multiplicado por draw

`D3D11BufferResource.UpdateConstantBuffer()` cria um novo array de 65.536 bytes, copia o trecho guest e chama `UpdateSubresource`. `SetData()` atualiza imediatamente todas as fatias sobrepostas. Há até oito fatias em cache por recurso, com remoção em lotes quando o limite é atingido.[^6]

Esse tamanho não deve ser reduzido cegamente: o shader traduzido pode declarar um cbuffer de 4.096 vetores, e uma redução anterior causou leituras zeradas e matrizes incorretas. A otimização precisa preservar o contrato do shader, incluindo o preenchimento da região não usada.

Como ilustração, não medição: 100 atualizações de 64 KiB por frame equivalem a 6,25 MiB/frame ou 375 MiB/s a 60 FPS, antes de contabilizar cópias adicionais e custos do driver. A Microsoft documenta custos extras de atualização sob contenção. [Microsoft: UpdateSubresource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-updatesubresource).

Há três etapas de implementação recomendadas. Primeiro, reutilizar armazenamento de upload e adiar a materialização até o bind que efetivamente o consome. Depois, coalescer atualizações consecutivas do mesmo intervalo, com geração e dirty ranges. Por fim, avaliar constant buffers dinâmicos/ring e offsets D3D11.1, consultando capacidades reais do dispositivo.

`WRITE_DISCARD` e `NO_OVERWRITE` não são substituições automáticas: é necessário preservar bytes ainda usados pela GPU e verificar suporte para constant buffers. [Microsoft: recursos dinâmicos](https://learn.microsoft.com/en-us/windows/win32/direct3d11/how-to--use-dynamic-resources).

A validação deve registrar bytes guest alterados versus bytes enviados, allocations por frame, criações de buffers e cache hit de fatias. Deve comparar pixels de matrizes, fontes e materiais, não apenas verificar ausência de exceções.

## 3. Readback: o presenter não é o único lugar onde ele acontece

O lote de Sonic e FireRed registra apresentação direta sem upload CPU. Isso é positivo, mas não significa que todo o renderer seja livre de readback. `D3D11BufferResource.GetData(offset,size)` copia o recurso inteiro para staging antes de mapear e extrair o intervalo solicitado.[^1][^6]

`D3D11Pipeline.ReadUInt32()` usa esse método para obter o contador de draws indiretos. Assim, ler quatro bytes pode copiar um buffer muito maior e aguardar a GPU. `CopyData()` já conhece o mirror CPU quando válido; essa informação deve ser aproveitada onde a semântica permitir.[^7]

Há ainda uma oportunidade concreta em `SetData`: ele chama `EnsureShadow` antes de escrever. Se a GPU invalidou o mirror, mesmo uma substituição integral por dados CPU pode primeiro baixar o conteúdo antigo inteiro. Uma escrita realmente completa pode dispensar a preservação do conteúdo antigo; escritas parciais precisam manter os bytes não substituídos.[^6]

Nas texturas, `ReadbackSubresource()` pode executar `CopyResource` antes de mapear somente um mip/layer. Há caminhos de conversão de índices UByte, quads/triangle fans e vértices half3 que usam dados CPU; quando o mirror está inválido, podem introduzir sincronização.[^7][^8]

O plano deve começar com uma classificação de readbacks por motivo: apresentação, constantes, contador indireto, repack, coerência guest e diagnóstico. Depois vêm staging por região e filas com consultas de conclusão. Nem todo readback pode ser adiado: se o guest precisa do valor imediatamente, é obrigatório respeitar essa dependência.

## 4. Memória e GC: evitar que a recuperação amplifique a pausa

O caminho de compilação HLSL chama `RuntimeMemoryBudget.Refresh()` e, sob pressão alta, `HandleMemoryTrimRequested()`. Esse handler limpa o cache DXBC em memória e, sob pressão crítica, solicita GC de geração 2 bloqueante. Em seguida, `EnterCompiler()` pode disparar o handler novamente e solicitar outro GC completo, respeitando seu próprio intervalo de dois segundos.[^9][^10]

Portanto, podem existir solicitações redundantes de coleta/trim na mesma sequência. Não significa que cada solicitação gere necessariamente a mesma pausa física; `Optimized` permite decisões do runtime. Ainda assim, a política deve ter um único responsável, registrar duração real e evitar disparos repetidos de componentes diferentes.

Limpar todo o DXBC também tem uma relação custo/benefício a medir: o ZIP auditado soma aproximadamente 2,76 MB, enquanto o processo pode consumir gigabytes. Esse tamanho não representa todo cache de todas as sessões, mas mostra por que a quantidade efetivamente recuperável importa. Preservar shaders quentes e descartar seletivamente tende a ser uma hipótese melhor que uma limpeza integral recorrente.[^1]

É necessário distinguir reserva virtual, commit, residência, payload útil e estimativas por categoria. Os contadores podem se sobrepor e não devem ser somados para simular o consumo do processo. O Xbox deve fornecer seu limite efetivo; a memória física total do Series S não é o orçamento integral do app.

As correções JIT recentes já deixaram de usar pressão externa como justificativa para purgar quase todo código útil. Ainda é necessário medir se manter mais traduções diminui recompilação sem exceder commit. O próximo passo de memória é orçamento por bytes recuperáveis, com histerese e trimming de recursos realmente descartáveis em limites seguros.

## 5. CPU: a segurança atual tem um custo arquitetural

A política segura mantém High-CQ e background translation desligados no lote. O código seleciona `CompilerOptions.None` para a tradução baseline; as opções High-CQ agregam otimização, SSA e alocação de registradores correspondente. Isso fornece uma hipótese forte para desempenho inferior ao de um emulador desktop otimizado, mas não mede quanto de cada jogo está limitado por qualidade de código.[^11]

O mecanismo de pins força o retorno ao dispatcher em branches que, no caminho tradicional, poderiam usar a tabela de funções. Isso evita manter endereços de retorno nativos para código que pode ser descartado. Não é seguro simplesmente retirar esse retorno, habilitar tailcalls globais ou reativar High-CQ para tentar recuperar os antigos 60 FPS.[^12]

Mesmo depois das correções recentes, o caminho quente executa consulta de switches, atualizações atômicas globais de diagnóstico, busca na árvore sob `ReaderWriterLockSlim`, manipulação de pins e timestamps. O lock de publicação ainda é global para a aquisição segura, embora já não cubra toda compilação fria.[^11][^13]

Recomenda-se medir primeiro o custo por milhão de dispatches e a contenção com múltiplos threads. Os candidatos de menor risco são telemetria acumulada por thread, amostragem de endereço/timestamp e cache de lookup com geração de invalidação. Toda cache de ponteiros precisa preservar aquisição atômica do pin: encontrar uma tradução não equivale a obter permissão para executá-la.

Somente depois dessa base deve ser comparado o dispatcher nativo com pins. Uma política de qualidade intermediária ou re-JIT limitado exigirá desenho próprio de publicação, orçamento e invalidação do PTC. O estágio de decodificação também depende da qualidade; trocar somente a enumeração de opções não garante equivalência ou ganho.

PTC deve ser avaliado separadamente em cold/warm startup. Ele evita retraduzir código persistido; não transforma automaticamente código baseline em High-CQ nem resolve todas as quedas em gameplay. Ativar PTC por padrão, se desejado, precisa incluir migração de configurações, identificação da política no cache e limites de pré-compilação.[^11]

## 6. Nenhum frame não é simplesmente FPS baixo

Os logs de Terraria e SMB35 apresentam trabalho inicial mínimo concluído e ausência de buffers produzidos. New Super Mario Bros. U Deluxe processa milhares de trabalhos sem apresentar frame. A falta de vídeo nesses casos não é demonstrada como falha do swapchain.[^1]

É necessário correlacionar PC/SVC do thread principal, estado de mutex/condvar, timers agendados e expirados, IPC, fila de comandos, buffers adquiridos/enfileirados e apresentação. Um thread aguardando no kernel pode ser legítimo; é a ausência de progresso das dependências que precisa ser identificada.

O watchdog atual já coleta telemetria do TimeManager, scheduler, compositor e FIFO, além das novas métricas JIT. A próxima captura deve usar essa correlação para localizar o primeiro ponto que para de progredir, preservando o erro original quando houver falha.[^14]

Limitar a 30 FPS, aumentar um timeout ou reapresentar o mesmo backbuffer não produz frames novos. Também não se deve classificar automaticamente toda tela branca/preta como vídeo FFmpeg: há transições normais e jogos que nunca chegaram à produção do primeiro frame.

## 7. Shaders: cache quente e frio exigem planos diferentes

O cache persistente atual é valioso e deve ser mantido. Entretanto, a construção de `D3D11Program` pode invocar tradução/compilação de shaders sincronicamente. O método `BackgroundContextAction` não constitui por si só uma fila de compilação independente: em parte dos casos executa a ação diretamente, e em outros encaminha uma interrupção.[^4][^15]

O plano deve separar tradução guest→IR, conversão SPIR-V→HLSL, compilação HLSL→DXBC e criação/bind do objeto D3D11. Tarefas CPU elegíveis podem usar um worker limitado por memória; chamadas ligadas ao contexto devem manter ownership. Um draw dependente de um shader ainda indisponível pode continuar tendo que aguardar: não existe garantia de eliminar o stall inicial sem pré-aquecimento.

Wonder somou cerca de 148 segundos em compilações HLSL no lote. Isso é diferente do Sonic lento sem compilação nova. Portanto, primeiro otimizar cold cache onde os contadores mostram esse custo; não usar essa hipótese como explicação universal.[^1]

## 8. Hazards e gerenciamento de estado

O código já compartilha snapshots por storage/mip/layer/formato e compara gerações. Isso deve ser preservado. Porém, `NotifyRenderTargetWrites()` marca todos os color targets e o depth target após draws, de forma conservadora. Essa marcação pode invalidar snapshots mesmo quando máscaras/estado impedem escrita efetiva; reduzir esse escopo exige uma prova baseada no estado, não adivinhar se algum pixel mudou.[^7][^8]

Rebinds percorrem ranges até o maior slot usado e há alocações de arrays para sanitização, limpeza e restauração. São candidatos a spans/buffers reutilizáveis e dirty tracking. O custo real depende da quantidade de draws, slots e transições por frame.

A política de snapshot no início do render pass não deve ser alterada só para reduzir números. Reutilizar conteúdo anterior quando o guest exige ler o resultado de um draw anterior pode melhorar o contador e estragar a imagem. É necessário um teste de feedback entre múltiplos draws, além de teste de subresources independentes.

O contador de cópias precisa informar bytes e motivo. O campo `copyMs` atual mede submissão CPU; não é duração da cópia na GPU. Contar menos chamadas ou medir uma função mais rápida no host não demonstra menor tempo de dispositivo.

## 9. Interop e alocações no D3D11

`D3D11Com.GetDelegate<T>()` chama `Marshal.GetDelegateForFunctionPointer<T>` em acessos de vtable usados por draws, binds e cópias. Há também arrays temporários nos binds de recursos, vértices e estado. Isso é um custo candidato em workload limitado pela CPU, principalmente com muitos draws pequenos.[^3][^7]

Não se deve afirmar, sem perfil, que o runtime necessariamente cria um delegate novo a cada chamada ou que esse é o principal gargalo. As opções são cache por endereço de função/vtable e tipo de assinatura, ou function pointers com ABI explícita, validadas em NativeAOT e no Xbox. O teste deve incluir lifetime do device e recriação após device loss.

## 10. Texturas, repacks e decodificação de vídeo

O backend informa ausência de suporte ASTC/ETC2; o caminho genérico contém decodificação para formato compatível e pode recomprimir ASTC para BC7 conforme a configuração. Isso pode afetar carregamento, memória e transições de cena. Não é evidência de que Sonic esteja limitado por ASTC.[^16]

Para conteúdo imutável, cachear conversões por identidade/geração pode evitar repetição. Para dados produzidos na GPU, investigar repack na GPU antes de readback. Formatos não suportados, mip/layer, precisão e swizzle devem permanecer corretos. A opção global que força single-sample também precisa ser documentada como compromisso visual, não como uma otimização sem custo de compatibilidade.[^17]

A integração FFmpeg usa `avcodec_send_packet`/`receive_frame` e superfícies de saída. O uso da classe NVDEC do guest não demonstra aceleração de vídeo pelo hardware host. Medir decodificação e upload é apropriado para cutscenes, mas não há neste lote evidência que justifique reescrever vídeo como solução geral de FPS.[^18]

## 11. Apresentação, VSync e frontend

O caminho nativo `PresentRendererFrame()` chama `Present(1,0)`. `D3D11Window.ChangeVSyncMode()` armazena a opção, mas ela não é propagada a esse argumento. Há, portanto, uma inconsistência de configuração a resolver; não é prova de que VSync cause 5 ou 13 FPS.[^19][^20]

É preciso medir bloqueio em Present, profundidade da fila e cadência do display. Desligar VSync indiscriminadamente pode alterar latência, tearing e uso de recursos; uma política explícita deve funcionar tanto no swapchain HWND quanto no de composição, respeitando capacidades.

O frontend continua usando um host UWP com `CompositionTarget.Rendering`, mas retorna antes do desenho do menu quando a sessão está ativa. O presenter nativo também evita redesenhar a shell durante a sessão. Permanecem polling de input e, se habilitado, overlay de FPS; não há evidência de um segundo menu inteiro sendo renderizado por baixo dos jogos no código atual.[^19][^21]

Não há justificativa de desempenho nesta auditoria para reverter ImGui, remover `SetMatrixTransform`, mudar resolução guest/crop ou reapresentar frames duplicados. O contador deve distinguir frames produzidos, submetidos e apresentados; contar refresh de UI como FPS do jogo invalida a medição.

## 12. Logs, áudio e acesso a arquivos

`CoreHostLog.Write` executa I/O síncrono sob lock, incluindo criação/verificação de diretório e append. `RuntimeDiagnosticLog` também escreve sincronicamente e consulta tamanho para rotação. Isso pode prolongar o trecho chamador, principalmente em armazenamento lento, mas o volume do lote não comprova sozinho uma queda de 60 para 5 FPS.[^22]

Uma fila limitada de escrita, batching e níveis de diagnóstico são candidatos. Ela deve preservar fatal/crash com flush controlado, contabilizar perdas quando cheia e não crescer sem limite. Diagnóstico detalhado deve ser ativado por janela curta; apagar todos os logs remove a capacidade de localizar falhas.

O áudio nativo já possui pool de buffers. Portanto, “implementar pooling de áudio” não é uma descoberta nova. No lado gerenciado, `GetSignalTelemetry` percorre bytes e samples de cada buffer, mesmo que a linha de log seja rara. Amostrar essa análise é uma otimização localizada; retirar callbacks de consumo ou fabricar avanço de áudio seria incorreto.[^23]

Não foi encontrada nesta análise uma medição de throughput/latência do armazenamento dos jogos que permita culpar o HD externo. Os testes precisam separar startup frio de execução sustentada, especialmente quando há PTC, shader cache, descompressão e logging competindo por I/O.

## Plano recomendado de execução

### Bloco A — correções determinísticas e segurança

Corrigir tamanho dos resultados de queries e auditar ResetCounter. Separar polling de espera obrigatória, eliminar heap/pin temporário no wrapper e retirar counters concluídos da lista de pendentes sem destruir objetos guest ainda vivos. Testar resultado, timeout, cancelamento e teardown antes de usar timings de GPU para comparação.

### Bloco B — desperdício de memória e transferências

Unificar o controlador de trim/GC, registrar bytes recuperados e duração. Reutilizar uploads de constantes, coalescer alterações e evitar readback em substituições completas. Implementar leitura de quatro bytes/intervalos sem copiar recursos inteiros quando possível. Preservar cbuffer de 64 KiB quando exigido pelo shader.

### Bloco C — throughput seguro de CPU

Publicar a rodada CPU/JIT já corrigida e acrescentar medição de contenção/dispatch por thread. Reduzir telemetria e buscas redundantes. Comparar baseline gerenciado com alternativa nativa que mantenha pins. Só depois avaliar maior qualidade de tradução, com orçamento e versionamento de cache próprios.

### Bloco D — caminhos gráficos dependentes de workload

Otimizar dirty state, interop, gerações de snapshots e conversões que forem dominantes nas medições. Criar pré-aquecimento de shaders limitado. Propagar corretamente a política de VSync. Não habilitar threading global ou remover barriers sem testes de ownership e sincronização.

### Bloco E — regressão no console

Usar o mesmo pacote identificado por hash, configuração, resolução e cena, distinguindo cache frio/quente. O conjunto mínimo deve conter Sonic Mania/FireRed, Wonder/Dead Cells, Terraria/New Super Mario Bros. U Deluxe, um outlier de hazards e uma baseline 3D antes funcional. Repetir sessões e voltar ao menu para detectar crescimento de memória e contaminação entre jogos.

## Critérios quantitativos de aceite

| Tema | Medida necessária | Aceite |
| --- | --- | --- |
| Progresso | Primeiro frame e sequência de buffers | Jogos antes parados precisam avançar; não basta mudar a porcentagem |
| Cadência | Frametime mediano, p95/p99 e intervalos acima de 100/1000 ms | Redução reproduzível de pausas, sem duplicar frames para elevar contador |
| CPU | Dispatches, tempo guest, compilação, espera por lock e scheduler | Identificar tempo dominante antes de mudar High-CQ |
| Queries | Pendentes, polling, espera obrigatória e alocações | Zero heap por tentativa; lista não cresce com todo histórico |
| Buffers | Bytes modificados, enviados e lidos de volta | Transferências proporcionais ao trabalho necessário, com pixels corretos |
| Memória | Commit, orçamento host, GC e bytes recuperados | Sem OOM/device loss, sem sequência repetida de GC sem recuperação útil |
| Hazards | Bytes por motivo/subresource e GPU timestamps | Redução sem feedback desatualizado ou SRVs anuladas |
| Apresentação | Frames produzidos/submetidos, Present ms e modo efetivo | Configuração aplicada e sem regressão de orientação, crop ou input |

60 FPS exige produzir cada frame no orçamento correspondente de aproximadamente 16,67 ms; um limiter não cria esse tempo disponível. O tamanho do NSP não fornece uma estimativa confiável desse custo. Nenhuma porcentagem de ganho global é certificada por esta auditoria.

## Fontes e referências

Os arquivos locais abaixo são fontes primárias da árvore corrente, consultada em 9 de setembro de 2026. As linhas identificam pontos de entrada; a numeração pode mudar em futuras edições. Não foram alterados código de produção, configurações ou pacotes nesta rodada de auditoria.

[^1]: Ryubox, [auditoria de Logs (6) e cache](</E:/ports/ryujinx uwp/docs/ASTRA6_LOGS6_AUDITORIA.md>) e [correções CPU/JIT posteriores](</E:/ports/ryujinx uwp/docs/ASTRA6_LOGS6_CORRECOES.md>). Fontes privadas: `Logs (6).zip`, `D3D11ShaderCache (1).zip`, `artifacts/logs6-audit-20260909/audit-summary.json`; plano fornecido `Ryubox_Astra6_Parte_2_D3D11_GPU.md`.
[^2]: Ryubox, [D3D11CounterEvent.Flush](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.D3D11/D3D11Resources.cs:2568>), seleção de query no construtor e tratamento de resultado.
[^3]: Ryubox, [D3D11Native.GetData](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.D3D11/D3D11Native.cs:1351>) e `D3D11Com.GetDelegate`, linha 811.
[^4]: Ryubox, [D3D11Renderer.UpdateCounters](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.D3D11/D3D11Renderer.cs:360>), `ReportCounter`, `ResetCounter`, `WaitSync`, `BackgroundContextAction` e `Dispose`.
[^5]: Ryubox, [ThreedClass](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.Gpu/Engine/Threed/ThreedClass.cs:473>), polling de counters; `SemaphoreUpdater.cs`, reset e utilização dos resultados.
[^6]: Ryubox, [D3D11BufferResource](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.D3D11/D3D11Resources.cs:103>), `SetData`, `GetConstantBuffer`, `GetData`, `UpdateConstantBuffer` e `EnsureShadow`.
[^7]: Ryubox, [D3D11Pipeline](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.D3D11/D3D11Pipeline.cs:157>), writes, indiretos, conversões de índices, rebinds e `ReadUInt32` na linha 3148.
[^8]: Ryubox, [D3D11Texture](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.D3D11/D3D11Resources.cs:1878>), snapshots; `ReadbackSubresource` na linha 2130. [D3D11Half3VertexBuffer](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.D3D11/D3D11Half3VertexBuffer.cs>).
[^9]: Ryubox, [CoreHostExports.CompileD3D11Hlsl](</E:/ports/ryujinx uwp/src/Ryujinx.Uwp.CoreHost/CoreHostExports.cs:2021>), cache e compilação; `HandleMemoryTrimRequested` na linha 2804.
[^10]: Ryubox, [RuntimeMemoryBudget.EnterCompiler](</E:/ports/ryujinx uwp/src/src/Ryujinx.Common/Memory/RuntimeMemoryBudget.cs:204>), `Refresh`, pressão, callbacks e coleta.
[^11]: Ryubox, [Translator](</E:/ports/ryujinx uwp/src/src/ARMeilleure/Translation/Translator.cs:413>), aquisição, instrumentação, opções e sincronização; [CompilerOptions](</E:/ports/ryujinx uwp/src/src/ARMeilleure/Translation/CompilerOptions.cs>) e [Compiler](</E:/ports/ryujinx uwp/src/src/ARMeilleure/Translation/Compiler.cs>).
[^12]: Ryubox, [InstEmitFlowHelper.EmitTableBranch](</E:/ports/ryujinx uwp/src/src/ARMeilleure/Instructions/InstEmitFlowHelper.cs:209>), retorno obrigatório sob pinned dispatch.
[^13]: Ryubox, [TranslatorCache](</E:/ports/ryujinx uwp/src/src/ARMeilleure/Translation/TranslatorCache.cs>), árvore e locks; [PinnedReclaimer](</E:/ports/ryujinx uwp/src/src/ARMeilleure/Translation/Cache/PinnedReclaimer.cs>), ownership.
[^14]: Ryubox, [watchdog e RunLoop](</E:/ports/ryujinx uwp/src/Ryujinx.Uwp.CoreHost/CoreHostExports.cs:1378>); [KTimeManager](</E:/ports/ryujinx uwp/src/src/Ryujinx.HLE/HOS/Kernel/Common/KTimeManager.cs>), timers; `KScheduler.cs` e `KThread.cs`, escalonamento e esperas.
[^15]: Ryubox, [D3D11Program](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.D3D11/D3D11Resources.cs:370>), construção, compilação e criação dos shaders.
[^16]: Ryubox, [conversão de texturas](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.Gpu/Image/Texture.cs:795>) e [Capabilities D3D11](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.D3D11/D3D11Renderer.cs:251>).
[^17]: Ryubox, [D3D11PerformanceSettings](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.D3D11/D3D11PerformanceSettings.cs>), single-sample e barriers.
[^18]: Ryubox, [FFmpegContext.DecodeFrame](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.Nvdec.FFmpeg/FFmpegContext.cs:168>).
[^19]: Ryubox, [PresentRendererFrame](</E:/ports/ryujinx uwp/src/Ryujinx.Uwp.Native/Ryujinx.Uwp.Native.cpp:1120>), ownership, overlay e Present.
[^20]: Ryubox, [D3D11Window](</E:/ports/ryujinx uwp/src/src/Ryujinx.Graphics.D3D11/D3D11Window.cs>), apresentação direta/compatível e `ChangeVSyncMode`.
[^21]: Ryubox, [MainPage.OnRendering](</E:/ports/ryujinx uwp/src/Ryujinx.Uwp.App/MainPage.xaml.cs:106>), isolamento da sessão; `UwpImGuiFrontendCoordinator.Pump`, linha 121.
[^22]: Ryubox, [CoreHostLog](</E:/ports/ryujinx uwp/src/Ryujinx.Uwp.CoreHost/CoreHostLog.cs:45>) e [RuntimeDiagnosticLog](</E:/ports/ryujinx uwp/src/src/Ryujinx.Common/Logging/RuntimeDiagnosticLog.cs>).
[^23]: Ryubox, [XAudio2UwpHardwareDeviceDriver](</E:/ports/ryujinx uwp/src/src/Ryujinx.Audio/Backends/XAudio2Uwp/XAudio2UwpHardwareDeviceDriver.cs:198>), análise de PCM; [pool nativo](</E:/ports/ryujinx uwp/src/Ryujinx.Uwp.Native/Ryujinx.Uwp.Native.cpp:2639>).

Referências públicas primárias, consultadas em 9 de setembro de 2026:

- Microsoft Learn. [D3D11_QUERY](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_query). Contratos de resultado de queries.
- Microsoft Learn. [ID3D11DeviceContext::GetData](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-getdata). Resultado disponível, polling e tipos.
- Microsoft Learn. [ID3D11DeviceContext::UpdateSubresource](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-updatesubresource). Atualização e contenção de recursos.
- Microsoft Learn. [How to use dynamic resources](https://learn.microsoft.com/en-us/windows/win32/direct3d11/how-to--use-dynamic-resources). Reutilização, discard/no-overwrite e consulta de capacidades.
