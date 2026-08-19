/* jsonrender: tiny htmx extension that renders JSON responses through
   a mustache <template>. Usage:
     <div hx-ext="jsonrender" hx-render="#myTemplate"
          hx-get="/api/..." hx-trigger="every 15s">loading…</div>
     <template id="myTemplate">{{#rows}}…{{/rows}}</template>
   Rendered nodes are passed through htmx.process() so hx-* attributes
   inside the template stay live. Zero build step, ~25 lines. */
htmx.defineExtension('jsonrender', {
  onEvent: function (name, evt) {
    if (name !== 'htmx:afterSwap') return;
    var el = evt.detail.elt;
    var tplSel = el.getAttribute('hx-render');
    if (!tplSel) return;
    var tpl = document.querySelector(tplSel) || el.querySelector('template');
    if (!tpl) return;
    var xhr = evt.detail.xhr;
    if (!xhr) return;
    var data;
    try { data = JSON.parse(xhr.responseText); } catch (e) { return; }
    if (data && !data.success) {
      el.innerHTML = '<div class="hint">' +
        (data.error || 'request failed') + '</div>';
      return;
    }
    el.innerHTML = Mustache.render(tpl.innerHTML, data || {});
    htmx.process(el);
  }
});