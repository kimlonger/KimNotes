export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.hostname !== 'note.kimlulu.com' || url.protocol !== 'https:') {
      url.hostname = 'note.kimlulu.com';
      url.protocol = 'https:';
      url.port = '';
      return Response.redirect(url.toString(), 301);
    }
    return env.ASSETS.fetch(request);
  },
};
