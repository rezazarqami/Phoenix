// HTML confirmation works in Android WebViews without JavaScript dialog support.
window.phoenixConfirm = text => new Promise(resolve => {
  const dialog = document.createElement('dialog');
  dialog.className = 'security-dialog';
  const content = document.createElement('div');
  content.className = 'dialog-body';
  const message = document.createElement('p');
  message.textContent = text;
  message.style.whiteSpace = 'pre-line';
  const accept = document.createElement('button');
  accept.type = 'button'; accept.className = 'primary'; accept.textContent = 'تأیید عملیات';
  const cancel = document.createElement('button');
  cancel.type = 'button'; cancel.textContent = 'انصراف';
  let settled = false;
  function finish(value) {
    if (settled) return;
    settled = true; dialog.close(); dialog.remove(); resolve(value);
  }
  accept.onclick = () => finish(true);
  cancel.onclick = () => finish(false);
  dialog.addEventListener('cancel', event => { event.preventDefault(); finish(false); });
  dialog.addEventListener('close', () => finish(false));
  content.append(message, accept, cancel); dialog.append(content);
  document.body.append(dialog); dialog.showModal(); cancel.focus();
});
