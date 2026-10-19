define(['loading', 'toast'], function (loading, toast) {
    'use strict';
    const pluginId = '1d46b74f-6daf-4c2c-a28f-52f202679c16';
    const kinds = { Probe: '探测', Encode: '编码' };
    const statuses = { Pending: '等待', Running: '运行中', Verifying: '校验中', Complete: '完成', Failed: '失败', Cancelled: '已取消' };
    // The text the server refuses an empty list with.
    const noQuality = '至少需要选择一个质量；如需停用请关闭“启用”。';
    // Outside development Jellyfin answers every exception with this body, which carries no reason.
    const noReason = 'Error processing request.';
    // The server's reason when it sent a short one, otherwise the generic text.
    async function saveError(error) {
        let body = '';
        if (error && typeof error.text === 'function') { try { body = (await error.text()).trim(); } catch (e) { body = ''; } }
        return body && body !== noReason && body.length < 200 ? '保存失败：' + body : '保存失败，请检查配置。';
    }
    return function (view) {
        let config;
        const fields = [...view.querySelectorAll('[data-field]')];
        function choices(selector, options, selected) {
            const container = view.querySelector(selector);
            container.replaceChildren();
            options.forEach(option => {
                const row = document.createElement('div'); row.className = 'checkboxContainer';
                const label = document.createElement('label');
                const input = document.createElement('input', { is: 'emby-checkbox' });
                input.setAttribute('is', 'emby-checkbox'); input.type = 'checkbox'; input.value = option.value;
                input.checked = selected.map(String).includes(String(option.value));
                const text = document.createElement('span'); text.textContent = option.text;
                label.append(input, text); row.append(label); container.append(row);
            });
        }
        const selected = selector => [...view.querySelectorAll(selector + ' input:checked')].map(el => el.value);
        function time(value) { const date = new Date(value); return value && !isNaN(date) ? date.toLocaleString() : ''; }
        function cell(row, text, title) {
            const td = document.createElement('td'); td.className = 'detailTableBodyCell'; td.textContent = text;
            if (title) td.title = title;
            row.append(td);
        }
        async function jobs() {
            const list = view.querySelector('.jobList');
            let result;
            try { result = await ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('Plugins/DistributedHls/Jobs', { limit: 200 }), dataType: 'json' }); }
            catch (error) {
                // 503: the database is unavailable, the status area says why.
                if (!error || error.status !== 503) throw error;
                const row = document.createElement('tr'); cell(row, '数据库不可用'); list.replaceChildren(row);
                return;
            }
            list.replaceChildren();
            result.forEach(job => {
                const row = document.createElement('tr'); row.className = 'detailTableBodyRow detailTableBodyRow-shaded';
                cell(row, kinds[job.Kind] || job.Kind);
                cell(row, statuses[job.Status] || job.Status);
                // The full item id and hash stay available as the tooltip.
                cell(row, (job.ItemId || job.Sha256 || '').slice(0, 12), [job.ItemId, job.Sha256].filter(Boolean).join('\n'));
                cell(row, String(job.Attempt));
                cell(row, job.Worker || '');
                cell(row, job.Error || '');
                cell(row, time(job.UpdatedAt));
                list.append(row);
            });
            if (!result.length) { const row = document.createElement('tr'); cell(row, '暂无任务'); list.append(row); }
        }
        async function status() {
            const result = await ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('Plugins/DistributedHls/Status'), dataType: 'json' });
            const counts = result.Counts || {};
            const problems = result.Problems || [];
            const last = result.LastReverify;
            const lines = [
                // First what stops the plugin (no database: no counts either) or its verification and deletion (the output root).
                ...(result.StoreError ? ['数据库不可用，插件以空数据运行：' + result.StoreError] : [
                    '任务：' + Object.keys(statuses).map(key => statuses[key] + ' ' + (counts[key] || 0)).join(' · '),
                    '媒体 ' + (counts.Media || 0) + ' · 已发布档位 ' + (counts.PublishedRenditions || 0) + ' · 待删除目录 ' + (result.PendingDeletions || 0)
                ]),
                ...(result.OutputRootProblem ? ['已暂停校验和删除：' + result.OutputRootProblem] : []),
                problems.length ? '接口自检发现问题：' : '接口自检：正常',
                ...problems.map(problem => '· ' + problem),
                last ? '上次校验已发布成果：' + time(last.At) + '，检查 ' + last.Checked + ' 个档位，失败 ' + last.Failed + ' 个' + (last.Aborted ? '；已中止：' + last.Aborted : '')
                    : '上次校验已发布成果：尚未执行'
            ];
            const box = view.querySelector('.hlsStatus'); box.replaceChildren();
            lines.forEach(text => { const line = document.createElement('p'); line.textContent = text; box.append(line); });
        }
        const refresh = () => Promise.all([status(), jobs()]);
        view.addEventListener('viewshow', async function () {
            loading.show();
            try {
                const result = await Promise.all([ApiClient.getPluginConfiguration(pluginId), ApiClient.getUsers(), ApiClient.getVirtualFolders()]);
                config = result[0];
                fields.forEach(field => { if (field.type === 'checkbox') field.checked = config[field.dataset.field]; else field.value = config[field.dataset.field]; });
                choices('.originalUsers', result[1].map(u => ({value: u.Id, text: u.Name})), config.OriginalUsers);
                choices('.libraries', result[2].map(l => ({value: l.ItemId, text: l.Name})), config.Libraries);
                choices('.qualities', [2160,1440,1080,720,480,360,240,144].map(q => ({value: q, text: q + 'p'})), config.Qualities);
            } catch (error) { toast('读取配置失败'); } finally { loading.hide(); }
            refresh().catch(() => toast('读取状态失败'));
        });
        view.querySelector('form').addEventListener('submit', async function (event) {
            event.preventDefault();
            const qualities = selected('.qualities').map(Number);
            if (!qualities.length) { toast(noQuality); return; }
            loading.show();
            fields.forEach(field => { config[field.dataset.field] = field.type === 'checkbox' ? field.checked : field.type === 'number' ? Number(field.value) : field.value; });
            config.OriginalUsers = selected('.originalUsers'); config.Libraries = selected('.libraries'); config.Qualities = qualities;
            try { await ApiClient.updatePluginConfiguration(pluginId, config); toast('设置已保存'); }
            catch (error) { toast(await saveError(error)); } finally { loading.hide(); }
        });
        view.querySelector('.refreshJobs').addEventListener('click', () => refresh().catch(() => toast('读取状态失败')));
        view.querySelector('.rebuildJobs').addEventListener('click', async function () {
            if (!confirm('将取消尚未领取的任务并重新计算，不会删除已发布成果')) return;
            loading.show();
            try { await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('Plugins/DistributedHls/Rebuild') }); toast('已请求重建工作列表，稍后刷新查看'); }
            catch (error) { toast('重建失败'); } finally { loading.hide(); }
            refresh().catch(() => toast('读取状态失败'));
        });
        view.querySelector('.verifyPublished').addEventListener('click', async function () {
            loading.show();
            try { await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('Plugins/DistributedHls/Verify') }); toast('已开始校验已发布成果，稍后刷新查看结果'); }
            catch (error) { toast(error && error.status === 409 ? '已有一次校验正在运行' : '校验请求失败'); } finally { loading.hide(); }
        });
    };
});
