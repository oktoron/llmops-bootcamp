// значення рядка в requests (модель, що відповіла; токени, вартість, статус) з результату кешу/виклику моделі.
// винесено з /chat у public static, щоб вибір моделі й правило NULL/0 покривали юніт-тести (див. GETTING_STARTED §10):
// 0/0 означає лише кеш, NULL — відповіді моделі з usage не було (деградація, очікування невдалого виклику).
// ціни лишаються в Program.cs (CostUsd(), шов W2) і приходять делегатом.
public static class RequestRow
{
    // модель, що фактично відповіла: після fallback це не та, яку обрав Route().
    // hit і деградація — обрана (на hit власного виклику не було, на деградації не відповіла жодна)
    public static string ServedModel(CacheLookup lookup, string routedModel) => lookup.Call?.ServedModel ?? routedModel;

    public static (int? PromptTokens, int? CompletionTokens, decimal? CostUsd, int Status) Compose(
        CacheLookup lookup, string servedModel, Func<string, int, int, decimal?> costUsd)
    {
        if (lookup.Source == CacheSource.Miss && lookup.Call is { } call)
        {
            // токени й вартість пише лише той запит, що сам викликав модель.
            // без usage (деградація: ланцюжок вичерпано) токени NULL, а не 0 — інакше рядок виглядав би як hit
            var (promptTokens, completionTokens) = call.UsageParsed
                ? ((int?, int?))(call.PromptTokens, call.CompletionTokens)
                : (null, null);
            // cost (W2): tokens * ціна моделі, що відповіла (див. CostUsd()): відповідь mock-mid після fallback
            // за цінами mock-strong завищила б /cost. NULL, якщо ціни чи usage немає (деградація — теж NULL)
            var cost = call.UsageParsed ? costUsd(servedModel, call.PromptTokens, call.CompletionTokens) : null;
            return (promptTokens, completionTokens, cost, call.Status);
        }

        // hit або очікування вдалого виклику (Joined): 200, 0 токенів і cost_usd = 0.
        // очікування невдалого виклику (JoinedFailed, у лічильниках це miss): статус лідера, токени й cost_usd = NULL —
        // щоб збій у W5 не порахувався як hit. правило збою одне — ResponseCache.Succeeded().
        var status = lookup.Call?.Status ?? 200;
        if (lookup.Source == CacheSource.JoinedFailed)
            return (null, null, null, status);
        return (0, 0, 0m, status);
    }
}
